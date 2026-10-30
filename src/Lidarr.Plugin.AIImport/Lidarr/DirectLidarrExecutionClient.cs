using System.Text.Json;
using System.Text.Json.Nodes;
using Lidarr.Plugin.AIImport.Approval;
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
        var current = _gateway.GetManualImports(plan.DownloadId);
        if (current.Count != plan.Files.Count)
            throw new StaleApprovalException("Lidarr manual-import revalidation returned an unexpected file count.");
        var byPath = current.ToDictionary(x => x.SourcePath ?? string.Empty, StringComparer.Ordinal);
        foreach (var approved in plan.Files)
        {
            if (!byPath.TryGetValue(approved.SourcePath, out var item) || item.ManualImportId != approved.ManualImportId ||
                item.Size != approved.Size || item.IndexerFlags != approved.IndexerFlags ||
                !string.Equals(item.DownloadId, approved.DownloadId, StringComparison.Ordinal) ||
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
            DownloadId = file.DownloadId,
            ReplaceExistingFiles = false,
            DisableReleaseSwitching = false
        }).ToList());
        if (reviewed.Count != plan.Files.Count)
            throw new StaleApprovalException("Lidarr authoritative manual-import review returned an unexpected file count.");
        var reviewedByPath = reviewed.ToDictionary(x => x.Path, StringComparer.Ordinal);
        foreach (var approved in plan.Files)
        {
            if (!reviewedByPath.TryGetValue(approved.SourcePath, out var item) ||
                item.Rejections?.Any() == true || item.Tracks.Count != 1 || item.Tracks[0].Id != approved.TrackId ||
                item.Artist?.Id != plan.ArtistId || item.Album?.Id != plan.AlbumId || item.Release?.Id != plan.ReleaseId)
                throw new StaleApprovalException("Lidarr rejected or changed the approved corrective mapping.");
        }
        if (HasOverlappingActiveManualImport(_commands.All(), plan.Files.Select(file => file.SourcePath).ToArray()))
            throw new StaleApprovalException("A queued or started manual-import command already overlaps this approval.");
        return Task.CompletedTask;
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
                DownloadId = file.DownloadId,
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
