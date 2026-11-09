using System.Text.Json;
using System.Text.Json.Nodes;
using Lidarr.Plugin.AIImport.Approval;
using Lidarr.Plugin.AIImport.Discovery;
using NzbDrone.Core.MediaFiles.TrackImport;
using NzbDrone.Core.MediaFiles.TrackImport.Manual;
using NzbDrone.Core.Messaging.Commands;
using NzbDrone.Core.Music;
using NzbDrone.Core.Qualities;

namespace Lidarr.Plugin.AIImport.Lidarr;

public sealed class DirectLidarrExecutionClient : ILidarrExecutionClient
{
    private readonly IInProcessLidarrGateway _gateway;
    private readonly IArtistService _artists;
    private readonly IAlbumService _albums;
    private readonly IReleaseService _releases;
    private readonly IManualImportService _manualImports;
    private readonly IManageCommandQueue _commands;
    private readonly InProcessImportSafety _safety;

    public DirectLidarrExecutionClient(
        IInProcessLidarrGateway gateway,
        IArtistService artists,
        IAlbumService albums,
        IReleaseService releases,
        IManualImportService manualImports,
        IManageCommandQueue commands,
        InProcessImportSafety safety)
    {
        _gateway = gateway;
        _artists = artists;
        _albums = albums;
        _releases = releases;
        _manualImports = manualImports;
        _commands = commands;
        _safety = safety;
    }

    public Task<string> GetArtistPathAsync(int artistId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (artistId <= 0) throw new ArgumentOutOfRangeException(nameof(artistId));
        var artist = _artists.GetArtist(artistId);
        if (artist.Id != artistId || string.IsNullOrWhiteSpace(artist.Path) || !Path.IsPathFullyQualified(artist.Path))
            throw new InvalidDataException("Lidarr artist path did not match the approved artist.");
        return Task.FromResult(Path.TrimEndingDirectorySeparator(artist.Path));
    }

    public Task RevalidateManualImportAsync(ApprovedImportPlan plan, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);
        cancellationToken.ThrowIfCancellationRequested();
        _safety.CaptureFingerprint();
        IReadOnlyList<SourceFileCandidate> current;
        if (plan.FolderPath is not null)
        {
            // Untracked folder sources are revalidated through the same reconciled
            // manual-import scan used by discovery and approval.
            var scan = _gateway.GetUntrackedManualImportScan(plan.FolderPath);
            if (scan.IndeterminateCount != 0)
                throw new StaleApprovalException("Current Lidarr import evidence is contradictory; reconcile and discover again.");
            current = scan.Files;
        }
        else
        {
            current = _gateway.GetManualImports(plan.DownloadId);
        }
        if (current.Count != plan.Files.Count)
            throw new StaleApprovalException("Lidarr manual-import revalidation returned an unexpected file count.");
        var byPath = current.ToDictionary(x => x.SourcePath ?? string.Empty, StringComparer.Ordinal);
        foreach (var approved in plan.Files)
        {
            if (!byPath.TryGetValue(approved.SourcePath, out var item) || item.ManualImportId != approved.ManualImportId ||
                item.Size != approved.Size || item.IndexerFlags != approved.IndexerFlags ||
                !string.Equals(item.DownloadId ?? string.Empty, approved.DownloadId, StringComparison.Ordinal) ||
                item.Quality is null || !JsonNode.DeepEquals(item.Quality, approved.Quality))
                throw new StaleApprovalException("Lidarr manual-import material changed.");
        }

        var artist = _artists.GetArtist(plan.ArtistId);
        var album = _albums.GetAlbum(plan.AlbumId);
        var release = _releases.GetRelease(plan.ReleaseId);
        if (artist.Id != plan.ArtistId || album.Id != plan.AlbumId || album.ArtistId != plan.ArtistId ||
            release.Id != plan.ReleaseId || release.AlbumId != plan.AlbumId)
            throw new StaleApprovalException("The approved Lidarr ownership relationship changed.");

        var reviewed = _manualImports.UpdateItems(plan.Files.Select(file => new ManualImportItem
        {
            Id = file.ManualImportId,
            Path = file.SourcePath,
            Artist = artist,
            Album = album,
            Release = release,
            Quality = JsonSerializer.Deserialize<QualityModel>(file.Quality.ToJsonString())
                ?? throw new InvalidDataException("Approved quality is malformed."),
            IndexerFlags = file.IndexerFlags,
            DownloadId = string.IsNullOrWhiteSpace(file.DownloadId) ? null : file.DownloadId,
            ReplaceExistingFiles = false,
            DisableReleaseSwitching = false
        }).ToList());
        var reviewedByPath = SelectAuthoritativeReviews(
            reviewed, plan.Files.Select(file => file.SourcePath).ToArray());
        foreach (var approved in plan.Files)
        {
            if (!reviewedByPath.TryGetValue(approved.SourcePath, out var item))
                throw new StaleApprovalException("Lidarr authoritative review omitted an approved source.");
            if (!MatchesApprovedReviewMaterial(item, approved))
                throw new StaleApprovalException("Lidarr authoritative review changed approved source material.");
            if (item.Artist?.Id != plan.ArtistId || item.Album?.Id != plan.AlbumId || item.Release?.Id != plan.ReleaseId)
                throw new StaleApprovalException("Lidarr authoritative review changed approved ownership.");
            if (!HasOnlyPermittedRejections(item, plan.HumanOverride))
                throw new StaleApprovalException("Lidarr authoritative review returned a non-identification safety rejection.");
            if (!plan.HumanOverride && (item.Tracks.Count != 1 || item.Tracks[0].Id != approved.TrackId))
                throw new StaleApprovalException("Lidarr authoritative review changed the approved automatic track mapping.");
        }
        if (HasOverlappingActiveManualImport(_commands.All(), plan.Files.Select(file => file.SourcePath).ToArray()))
            throw new StaleApprovalException("A queued or started manual-import command already overlaps this approval.");
        return Task.CompletedTask;
    }

    internal static IReadOnlyDictionary<string, ManualImportItem> SelectAuthoritativeReviews(
        IReadOnlyCollection<ManualImportItem> reviewed,
        IReadOnlyCollection<string> approvedSourcePaths)
    {
        var approved = approvedSourcePaths.ToHashSet(StringComparer.Ordinal);
        if (approved.Count != approvedSourcePaths.Count ||
            approved.Any(path => string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path)))
            throw new StaleApprovalException("The approved manual-import source set is invalid.");

        var extras = reviewed.Where(item => !approved.Contains(item.Path)).ToArray();
        if (extras.Any(item => !item.AdditionalFile || !string.IsNullOrWhiteSpace(item.DownloadId) ||
                              string.IsNullOrWhiteSpace(item.Path) || !Path.IsPathFullyQualified(item.Path)) ||
            extras.Select(item => item.Path).Distinct(StringComparer.Ordinal).Count() != extras.Length)
        {
            throw new StaleApprovalException("Lidarr authoritative manual-import review returned an unverified unrelated source.");
        }

        var selected = new Dictionary<string, ManualImportItem>(StringComparer.Ordinal);
        foreach (var sourcePath in approved)
        {
            var matches = reviewed.Where(item => string.Equals(item.Path, sourcePath, StringComparison.Ordinal)).ToArray();
            if (matches.Length != 1)
                throw new StaleApprovalException("Lidarr authoritative manual-import review did not return exactly one approved source.");
            selected.Add(sourcePath, matches[0]);
        }

        return selected;
    }

    internal static bool MatchesApprovedReviewMaterial(ManualImportItem item, ApprovedImportFile approved)
    {
        return item.Id == approved.ManualImportId &&
               string.Equals(item.Path, approved.SourcePath, StringComparison.Ordinal) &&
               string.Equals(item.DownloadId ?? string.Empty, approved.DownloadId, StringComparison.Ordinal) &&
               item.Size == approved.Size && item.IndexerFlags == approved.IndexerFlags &&
               !item.AdditionalFile && !item.ReplaceExistingFiles && !item.DisableReleaseSwitching &&
               item.Quality is not null &&
               JsonNode.DeepEquals(JsonSerializer.SerializeToNode(item.Quality), approved.Quality);
    }

    internal static bool MatchesApprovedCorrectiveMapping(
        ManualImportItem item,
        bool humanOverride,
        int artistId,
        int albumId,
        int releaseId,
        int approvedTrackId)
    {
        if (!HasOnlyPermittedRejections(item, humanOverride) || item.Artist?.Id != artistId ||
            item.Album?.Id != albumId || item.Release?.Id != releaseId)
            return false;

        // Lidarr's UpdateItems endpoint recalculates tracks from tags and has no input for the
        // user's selected track IDs. A provenance-bound human override was already checked
        // against the current release/track candidates, so require Lidarr to accept the source,
        // artist, album, and release without treating its automatic track guess as authoritative.
        return humanOverride || item.Tracks.Count == 1 && item.Tracks[0].Id == approvedTrackId;
    }

    internal static bool HasOnlyPermittedRejections(ManualImportItem item, bool humanOverride)
    {
        var rejections = item.Rejections?.ToArray() ?? [];
        if (rejections.Length == 0) return true;
        if (!humanOverride) return false;

        // A complete provenance-bound human mapping replaces only Lidarr's tag-distance and
        // automatic track-completeness judgement. Import safety and library-state rejections
        // (free space, upgrades, existing imports, roots, unpacking, and similar) still block.
        return rejections.All(rejection => IsAutomaticIdentificationRejection(rejection.Reason));
    }

    private static bool IsAutomaticIdentificationRejection(string? reason)
    {
        if (string.IsNullOrWhiteSpace(reason)) return false;
        return reason.StartsWith("Couldn't find similar album for ", StringComparison.Ordinal) ||
               reason.StartsWith("Album match is not close enough:", StringComparison.Ordinal) ||
               string.Equals(reason, "No tracks matched", StringComparison.Ordinal) ||
               reason.StartsWith("Worst track match:", StringComparison.Ordinal) ||
               reason.StartsWith("Track match is not close enough:", StringComparison.Ordinal) ||
               string.Equals(reason, "Has unmatched tracks", StringComparison.Ordinal) ||
               string.Equals(reason, "Has missing tracks", StringComparison.Ordinal) ||
               reason.StartsWith("Couldn't parse track from:", StringComparison.Ordinal);
    }


    internal static bool HasOverlappingActiveManualImport(
        IEnumerable<CommandModel> commands,
        IReadOnlyCollection<string> approvedSourcePaths)
    {
        var approved = approvedSourcePaths.ToHashSet(StringComparer.Ordinal);
        return commands.Any(model =>
            model.Status is CommandStatus.Queued or CommandStatus.Started &&
            model.Body is ManualImportCommand manual &&
            manual.Files is not null &&
            manual.Files.Any(file => !string.IsNullOrWhiteSpace(file.Path) && approved.Contains(file.Path)));
    }

    public Task<LidarrCommandReceipt> SubmitManualImportAsync(ApprovedImportPlan plan, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);
        cancellationToken.ThrowIfCancellationRequested();
        _safety.CaptureFingerprint();
        if (plan.Files.Count == 0 || plan.Files.Select(x => x.SourcePath).Distinct(StringComparer.Ordinal).Count() != plan.Files.Count ||
            plan.Files.Select(x => x.TrackId).Distinct().Count() != plan.Files.Count)
            throw new InvalidDataException("Approved import plan is incomplete or not one-to-one.");
        var command = new ManualImportCommand
        {
            ImportMode = ImportMode.Copy,
            ReplaceExistingFiles = false,
            Files = plan.Files.Select(file => (ManualImportFile)new FreshManualImportFile
            {
                Path = file.SourcePath,
                ArtistId = plan.ArtistId,
                AlbumId = plan.AlbumId,
                AlbumReleaseId = plan.ReleaseId,
                TrackIds = [file.TrackId],
                Quality = JsonSerializer.Deserialize<QualityModel>(file.Quality.ToJsonString())
                    ?? throw new InvalidDataException("Approved quality is malformed."),
                IndexerFlags = file.IndexerFlags,
                DownloadId = string.IsNullOrWhiteSpace(file.DownloadId) ? null : file.DownloadId,
                DisableReleaseSwitching = false
            }).ToList()
        };
        var queued = _commands.Push(command, CommandPriority.High);
        if (!ReferenceEquals(queued.Body, command))
            throw new InvalidDataException("Lidarr returned an existing manual-import command instead of creating a fresh command.");
        if (queued.Id <= 0 || !string.Equals(queued.Name, "ManualImport", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Lidarr did not return a valid manual-import command receipt.");
        return Task.FromResult(new LidarrCommandReceipt(queued.Id, queued.Status.ToString().ToLowerInvariant()));
    }
}

internal sealed class FreshManualImportFile : ManualImportFile
{
    private readonly Guid _submissionIdentity = Guid.NewGuid();

    public override bool Equals(object? obj) => ReferenceEquals(this, obj);
    public override int GetHashCode() => _submissionIdentity.GetHashCode();
}
