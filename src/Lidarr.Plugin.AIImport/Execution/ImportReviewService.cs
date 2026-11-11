using System.Text.Json;
using System.Text.Json.Nodes;
using Lidarr.Plugin.AIImport.Approval;
using Lidarr.Plugin.AIImport.Filesystem;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Music;

namespace Lidarr.Plugin.AIImport.Execution;

public sealed record ImportReviewFileDto(
    int HistoryId,
    int TrackFileId,
    int TrackId,
    string SourcePath,
    string ImportedPath,
    long Size,
    JsonObject Quality,
    int IndexerFlags);

public sealed record ImportReviewDto(
    string ApprovalId,
    string AnalysisId,
    ApprovalState State,
    DateTimeOffset CreatedAt,
    DateTimeOffset? CompletedAt,
    DateTimeOffset? RolledBackAt,
    int ArtistId,
    int AlbumId,
    int ReleaseId,
    string DownloadId,
    int? CommandId,
    string? Outcome,
    DateTimeOffset? ActivityCleanupStartedAt,
    DateTimeOffset? ActivityCleanupCompletedAt,
    string? ActivityCleanupOutcome,
    IReadOnlyList<ImportReviewFileDto> Files,
    bool Legacy,
    bool CanRollback);

public sealed record ImportRollbackResult(string ApprovalId, int RemovedFiles, string Message);

public sealed class ImportReviewService
{
    private readonly ApprovalStore _store;
    private readonly IMediaFileService _mediaFiles;
    private readonly ITrackService _tracks;
    private readonly IFileIdentityProvider _identities;
    private readonly INativeHardlinkRollback _unlink;

    public ImportReviewService(
        ApprovalStore store,
        IMediaFileService mediaFiles,
        ITrackService tracks,
        IFileIdentityProvider identities,
        INativeHardlinkRollback unlink)
    {
        _store = store;
        _mediaFiles = mediaFiles;
        _tracks = tracks;
        _identities = identities;
        _unlink = unlink;
    }

    public IReadOnlyList<ImportReviewDto> List() =>
        _store.GetReviewHistoryAsync(CancellationToken.None).GetAwaiter().GetResult().Select(ToReview).ToArray();

    public ImportRollbackResult Rollback(string approvalId, string confirmation)
    {
        if (!string.Equals(confirmation, "ROLLBACK", StringComparison.Ordinal))
            throw new ArgumentException("The exact rollback confirmation is required.", nameof(confirmation));
        var record = _store.GetAsync(approvalId, CancellationToken.None).GetAwaiter().GetResult()
            ?? throw new KeyNotFoundException("Import review record was not found.");
        if (record.State != ApprovalState.Succeeded || record.ReconciledFiles is not { Count: > 0 })
            throw new InvalidOperationException("Only a completely reconciled non-legacy import can be rolled back.");
        var plan = DeserializePlan(record);
        var evidence = record.ReconciledFiles.OrderBy(file => file.TrackFileId).ToArray();
        if (evidence.Length != plan.Files.Count || evidence.Select(x => x.SourceId).ToHashSet(StringComparer.Ordinal)
                .SetEquals(plan.Files.Select(x => x.SourceId)) == false)
            throw new InvalidDataException("Rollback evidence is incomplete.");

        var trackFiles = LoadAndValidateLidarrState(plan, evidence);

        if (_store.HasConflictingReconciliationEvidenceAsync(approvalId, evidence, CancellationToken.None)
            .GetAwaiter().GetResult())
            throw new InvalidOperationException("An audited destination or track-file row is shared by another import record.");

        foreach (var item in evidence)
        {
            var approved = plan.Files.Single(file => string.Equals(file.SourceId, item.SourceId, StringComparison.Ordinal));
            if (approved.TrackId != item.TrackId || approved.Size != item.Size || approved.IndexerFlags != item.IndexerFlags ||
                !string.Equals(approved.SourcePath, item.SourcePath, StringComparison.Ordinal) ||
                !JsonNode.DeepEquals(approved.Quality, item.Quality) ||
                !IsBelowRoot(item.ImportedPath, plan.DestinationRoot))
                throw new InvalidOperationException("Current Lidarr state no longer exactly matches the audited import; rollback was not started.");
            var source = _identities.InspectAsync(item.SourcePath, CancellationToken.None).GetAwaiter().GetResult();
            var destination = _identities.InspectAsync(item.ImportedPath, CancellationToken.None).GetAwaiter().GetResult();
            if (source.Device != item.SourceDevice || source.Inode != item.SourceInode || source.Size != item.Size ||
                destination.Device != item.DestinationDevice || destination.Inode != item.DestinationInode || destination.Size != item.Size ||
                source.Device != destination.Device || source.Inode != destination.Inode ||
                approved.Identity.Device != item.SourceDevice || approved.Identity.Inode != item.SourceInode)
                throw new InvalidOperationException("Source or destination identity changed; rollback was not started.");
        }

        _store.BeginRollbackAsync(approvalId, CancellationToken.None).GetAwaiter().GetResult();
        var removed = 0;
        try
        {
            foreach (var item in evidence)
            {
                _unlink.UnlinkAsync(item.SourcePath, item.ImportedPath, item.DestinationDevice,
                    item.DestinationInode, item.Size, CancellationToken.None).GetAwaiter().GetResult();
                removed++;
            }
            // Re-read every Lidarr row and binding after the filesystem phase and
            // immediately before deleting database state. Any concurrent rescan,
            // upgrade, retag, or rebind makes rollback indeterminate instead.
            trackFiles = LoadAndValidateLidarrState(plan, evidence);
            _mediaFiles.DeleteMany(trackFiles.ToList(), DeleteMediaFileReason.ManualOverride);

            if (_mediaFiles.Get(evidence.Select(file => file.TrackFileId).ToList()).Count != 0 ||
                evidence.Any(file => File.Exists(file.ImportedPath)) ||
                evidence.Any(file => _tracks.GetTracksByFileId(file.TrackFileId).Count != 0))
                throw new InvalidOperationException("Post-verification found remaining destination, track-file, or track binding state.");
            foreach (var item in evidence)
            {
                var source = _identities.InspectAsync(item.SourcePath, CancellationToken.None).GetAwaiter().GetResult();
                if (source.Device != item.SourceDevice || source.Inode != item.SourceInode || source.Size != item.Size)
                    throw new InvalidOperationException("A source file changed during rollback.");
            }

            var message = $"Rolled back {removed} imported track file(s). Source data and library metadata were retained.";
            _store.CompleteRollbackAsync(approvalId, true, message, CancellationToken.None).GetAwaiter().GetResult();
            return new ImportRollbackResult(approvalId, removed, message);
        }
        catch (Exception exception)
        {
            _store.CompleteRollbackAsync(approvalId, false,
                $"Rollback became indeterminate after {removed} exact unlink(s): {SafeMessage(exception)} Manual reconciliation is required.",
                CancellationToken.None).GetAwaiter().GetResult();
            throw new InvalidOperationException("Rollback is indeterminate and must not be retried automatically.", exception);
        }
    }

    private TrackFile[] LoadAndValidateLidarrState(
        ApprovedImportPlan plan,
        IReadOnlyList<ReconciledImportFile> evidence)
    {
        var trackFiles = _mediaFiles.Get(evidence.Select(file => file.TrackFileId).ToList())
            .OrderBy(file => file.Id).ToArray();
        if (trackFiles.Length != evidence.Count || trackFiles.Select(file => file.Id).Distinct().Count() != evidence.Count)
            throw new InvalidOperationException("One or more audited track-file rows changed.");
        foreach (var item in evidence)
        {
            var trackFile = trackFiles.Single(file => file.Id == item.TrackFileId);
            var consumers = _tracks.GetTracksByFileId(item.TrackFileId);
            if (trackFile.AlbumId != plan.AlbumId || trackFile.Size != item.Size ||
                (int)trackFile.IndexerFlags != item.IndexerFlags ||
                !string.Equals(trackFile.Path, item.ImportedPath, StringComparison.Ordinal) ||
                !JsonNode.DeepEquals(JsonSerializer.SerializeToNode(trackFile.Quality), item.Quality) ||
                consumers.Count != 1 || consumers[0].Id != item.TrackId || consumers[0].AlbumId != plan.AlbumId ||
                consumers[0].Artist?.Value?.Id != plan.ArtistId || !consumers[0].HasFile ||
                consumers[0].TrackFileId != item.TrackFileId)
                throw new InvalidOperationException("Current Lidarr state no longer exactly matches the audited import.");
        }
        return trackFiles;
    }

    private static ImportReviewDto ToReview(ApprovalRecord record)
    {
        var plan = DeserializePlan(record);
        var files = (record.ReconciledFiles ?? Array.Empty<ReconciledImportFile>())
            .Select(file => new ImportReviewFileDto(file.HistoryId, file.TrackFileId, file.TrackId,
                file.SourcePath, file.ImportedPath, file.Size, file.Quality, file.IndexerFlags)).ToArray();
        var legacy = record.State == ApprovalState.Succeeded && files.Length == 0;
        return new ImportReviewDto(record.ApprovalId, record.AnalysisId, record.State, record.CreatedAt,
            record.CompletedAt, record.RolledBackAt, plan.ArtistId, plan.AlbumId, plan.ReleaseId,
            plan.DownloadId, record.CommandId, record.Outcome,
            record.ActivityCleanupStartedAt, record.ActivityCleanupCompletedAt, record.ActivityCleanupOutcome,
            files, legacy,
            record.State == ApprovalState.Succeeded && !legacy && files.Length == plan.Files.Count);
    }

    private static ApprovedImportPlan DeserializePlan(ApprovalRecord record) =>
        record.Payload.Deserialize<ApprovedImportPlan>() ?? throw new InvalidDataException("Import review payload is malformed.");

    private static bool IsBelowRoot(string path, string root)
    {
        if (string.IsNullOrWhiteSpace(root) || !Path.IsPathFullyQualified(path) || !Path.IsPathFullyQualified(root)) return false;
        var relative = Path.GetRelativePath(Path.GetFullPath(root), Path.GetFullPath(path));
        return relative != "." && !Path.IsPathFullyQualified(relative) && relative != ".." &&
               !relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal) &&
               !relative.StartsWith($"..{Path.AltDirectorySeparatorChar}", StringComparison.Ordinal);
    }

    private static string SafeMessage(Exception exception)
    {
        var message = exception.Message.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return message.Length <= 300 ? message : message[..300];
    }
}
