using System.Text.Json.Nodes;
using Lidarr.Plugin.AIImport.Approval;
using Lidarr.Plugin.AIImport.Authorization;

namespace Lidarr.Plugin.AIImport.Execution;

public sealed record LidarrCommandState(int CommandId, string Name, string Status, string Result);

public sealed record LidarrImportHistoryEvidence(
    int HistoryId,
    int ArtistId,
    int AlbumId,
    int TrackId,
    string DownloadId,
    DateTimeOffset Date,
    int TrackFileId,
    string DroppedPath,
    string ImportedPath,
    long Size,
    int IndexerFlags,
    JsonObject Quality);

public sealed record LidarrTrackFileEvidence(
    int TrackFileId,
    int ArtistId,
    int AlbumId,
    string Path,
    long Size,
    JsonObject Quality,
    int IndexerFlags);

public sealed record LidarrTrackBinding(
    int TrackId,
    int ArtistId,
    int AlbumId,
    int TrackFileId,
    bool HasFile);

public interface ILidarrReconciliationClient
{
    Task<LidarrCommandState> GetCommandAsync(int commandId, CancellationToken cancellationToken);
    Task<IReadOnlyList<LidarrImportHistoryEvidence>> GetImportHistoryAsync(
        string downloadId,
        int albumId,
        CancellationToken cancellationToken);
    Task<IReadOnlyList<LidarrTrackFileEvidence>> GetTrackFilesAsync(
        int albumId,
        IReadOnlyList<int> trackFileIds,
        CancellationToken cancellationToken);
    Task<IReadOnlyList<LidarrTrackBinding>> GetTracksAsync(
        IReadOnlyList<int> trackIds,
        CancellationToken cancellationToken);
}

public enum ImportReconciliationStatus
{
    Pending,
    Succeeded
}

public sealed record ImportReconciliationResult(
    ImportReconciliationStatus Status,
    int VerifiedFiles,
    string Message);

public sealed class ImportReconciliationException(string message) : Exception(message);

public interface IImportReconciliationService
{
    Task<ImportReconciliationResult> ReconcileAsync(
        ApprovedImportPlan plan,
        IReadOnlyList<ApprovalAuthorizationBinding> authorizationBindings,
        int? commandId,
        DateTimeOffset executionStartedAt,
        CancellationToken cancellationToken);
}

public sealed class ImportReconciliationService : IImportReconciliationService
{
    private readonly ILidarrReconciliationClient _lidarr;
    private readonly ITransferResultVerifier _transfers;

    public ImportReconciliationService(
        ILidarrReconciliationClient lidarr,
        ITransferResultVerifier transfers)
    {
        _lidarr = lidarr;
        _transfers = transfers;
    }

    public async Task<ImportReconciliationResult> ReconcileAsync(
        ApprovedImportPlan plan,
        IReadOnlyList<ApprovalAuthorizationBinding> authorizationBindings,
        int? commandId,
        DateTimeOffset executionStartedAt,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (plan.Files.Count == 0 || authorizationBindings.Count != plan.Files.Count || commandId is <= 0)
            throw new ArgumentException("Reconciliation requires one authorization per approved file and a positive command ID when a receipt was recorded.");
        if (authorizationBindings.Select(binding => binding.AuthorizationId).Distinct(StringComparer.Ordinal).Count() != authorizationBindings.Count ||
            authorizationBindings.Select(binding => binding.SourceId).Distinct(StringComparer.Ordinal).Count() != authorizationBindings.Count)
            throw new ArgumentException("Reconciliation authorization bindings must be unique.", nameof(authorizationBindings));
        var bindingsBySourceId = authorizationBindings.ToDictionary(binding => binding.SourceId, StringComparer.Ordinal);

        if (commandId is not null)
        {
            var command = await _lidarr.GetCommandAsync(commandId.Value, cancellationToken).ConfigureAwait(false);
            if (command.CommandId != commandId.Value || !string.Equals(command.Name, "ManualImport", StringComparison.OrdinalIgnoreCase))
                throw new ImportReconciliationException("Lidarr command identity does not match the approved manual import.");
            if (string.Equals(command.Status, "queued", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(command.Status, "started", StringComparison.OrdinalIgnoreCase))
                return new ImportReconciliationResult(ImportReconciliationStatus.Pending, 0, "Lidarr command is still running.");
            var terminalStatuses = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "completed", "failed", "aborted", "cancelled", "orphaned"
            };
            if (!terminalStatuses.Contains(command.Status))
                throw new ImportReconciliationException(
                    $"Lidarr command returned unknown status {command.Status}; external state requires manual reconciliation.");
        }

        var history = await _lidarr.GetImportHistoryAsync(plan.DownloadId, plan.AlbumId, cancellationToken).ConfigureAwait(false);
        var orderedFiles = plan.Files.OrderBy(file => file.SourceId, StringComparer.Ordinal).ToArray();
        var selectedHistory = new List<LidarrImportHistoryEvidence>(orderedFiles.Length);
        foreach (var file in orderedFiles)
        {
            var matches = history.Where(item =>
                    item.Date >= executionStartedAt &&
                    item.ArtistId == plan.ArtistId && item.AlbumId == plan.AlbumId && item.TrackId == file.TrackId &&
                    string.Equals(item.DownloadId, file.DownloadId, StringComparison.Ordinal) &&
                    string.Equals(item.DroppedPath, file.SourcePath, StringComparison.Ordinal) &&
                    item.Size == file.Size && item.IndexerFlags == file.IndexerFlags &&
                    JsonNode.DeepEquals(item.Quality, file.Quality))
                .ToArray();
            if (matches.Length != 1)
                throw new ImportReconciliationException(
                    "Lidarr history did not contain exactly one matching import event for every approved source file.");
            var evidence = matches[0];
            if (evidence.HistoryId <= 0 || evidence.TrackFileId <= 0 ||
                string.IsNullOrWhiteSpace(evidence.ImportedPath) || !Path.IsPathFullyQualified(evidence.ImportedPath))
                throw new ImportReconciliationException("Lidarr history import evidence is incomplete.");
            selectedHistory.Add(evidence);
        }
        if (selectedHistory.Select(item => item.HistoryId).Distinct().Count() != selectedHistory.Count ||
            selectedHistory.Select(item => item.TrackFileId).Distinct().Count() != selectedHistory.Count ||
            selectedHistory.Select(item => item.ImportedPath).Distinct(StringComparer.Ordinal).Count() != selectedHistory.Count)
            throw new ImportReconciliationException("Lidarr history reused an event, track file, or destination path.");

        var trackFileIds = selectedHistory.Select(item => item.TrackFileId).ToArray();
        var trackFiles = await _lidarr.GetTrackFilesAsync(plan.AlbumId, trackFileIds, cancellationToken).ConfigureAwait(false);
        if (trackFiles.Count != trackFileIds.Length || trackFiles.Select(item => item.TrackFileId).Distinct().Count() != trackFiles.Count)
            throw new ImportReconciliationException("Lidarr did not return every imported track file uniquely.");
        var trackFilesById = trackFiles.ToDictionary(item => item.TrackFileId);

        var trackIds = orderedFiles.Select(file => file.TrackId).ToArray();
        var tracks = await _lidarr.GetTracksAsync(trackIds, cancellationToken).ConfigureAwait(false);
        if (tracks.Count != trackIds.Length || tracks.Select(item => item.TrackId).Distinct().Count() != tracks.Count)
            throw new ImportReconciliationException("Lidarr did not return every approved track uniquely.");
        var tracksById = tracks.ToDictionary(item => item.TrackId);

        for (var index = 0; index < orderedFiles.Length; index++)
        {
            var approved = orderedFiles[index];
            var imported = selectedHistory[index];
            if (!bindingsBySourceId.TryGetValue(approved.SourceId, out var binding) ||
                binding.TrackId != approved.TrackId ||
                !string.Equals(binding.SourcePath, approved.SourcePath, StringComparison.Ordinal) ||
                binding.SourceDevice != approved.Identity.Device || binding.SourceInode != approved.Identity.Inode)
                throw new ImportReconciliationException("Durable native authorization binding does not match the approved source file.");
            if (!trackFilesById.TryGetValue(imported.TrackFileId, out var trackFile) ||
                trackFile.ArtistId != plan.ArtistId || trackFile.AlbumId != plan.AlbumId ||
                !string.Equals(trackFile.Path, imported.ImportedPath, StringComparison.Ordinal) ||
                trackFile.Size != approved.Size || trackFile.IndexerFlags != approved.IndexerFlags ||
                !JsonNode.DeepEquals(trackFile.Quality, approved.Quality))
                throw new ImportReconciliationException("Lidarr track-file state does not match approved import evidence.");
            if (!tracksById.TryGetValue(approved.TrackId, out var track) || !track.HasFile ||
                track.ArtistId != plan.ArtistId || track.AlbumId != plan.AlbumId ||
                track.TrackFileId != imported.TrackFileId)
                throw new ImportReconciliationException("Lidarr track binding does not match the approved track file.");

            var transfer = await _transfers.VerifyAsync(
                binding.AuthorizationId, imported.ImportedPath, cancellationToken).ConfigureAwait(false);
            if (transfer.SourceDevice != approved.Identity.Device || transfer.SourceInode != approved.Identity.Inode)
                throw new ImportReconciliationException("Native transfer result does not match the approved source inode.");
        }

        return new ImportReconciliationResult(
            ImportReconciliationStatus.Succeeded,
            orderedFiles.Length,
            commandId is null
                ? "The command receipt was unavailable, but every approved file was verified in native transfer state, filesystem state, Lidarr history, track-file state, and track bindings."
                : "Every approved file was verified in native transfer state, filesystem state, Lidarr history, track-file state, and track bindings.");
    }
}
