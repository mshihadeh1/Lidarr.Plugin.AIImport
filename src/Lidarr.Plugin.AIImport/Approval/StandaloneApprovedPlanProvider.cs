using Lidarr.Plugin.AIImport.Discovery;
using Lidarr.Plugin.AIImport.Filesystem;
using Lidarr.Plugin.AIImport.Lidarr;
using Lidarr.Plugin.AIImport.Matching;
using Lidarr.Plugin.AIImport.Workflows;

namespace Lidarr.Plugin.AIImport.Approval;

public sealed class StandaloneApprovedPlanProvider : IApprovedPlanProvider
{
    private readonly StandaloneAnalysisStore _analyses;
    private readonly IInProcessLidarrGateway _gateway;
    private readonly IFileIdentityProvider _identities;
    private readonly ILidarrExecutionClient _execution;
    private readonly Func<string> _captureSafetyFingerprint;

    public StandaloneApprovedPlanProvider(
        StandaloneAnalysisStore analyses,
        IInProcessLidarrGateway gateway,
        IFileIdentityProvider identities,
        ILidarrExecutionClient execution,
        InProcessImportSafety safety)
        : this(analyses, gateway, identities, execution, safety.CaptureFingerprint)
    {
    }

    internal StandaloneApprovedPlanProvider(
        StandaloneAnalysisStore analyses,
        IInProcessLidarrGateway gateway,
        IFileIdentityProvider identities,
        ILidarrExecutionClient execution,
        Func<string> captureSafetyFingerprint)
    {
        _analyses = analyses;
        _gateway = gateway;
        _identities = identities;
        _execution = execution;
        _captureSafetyFingerprint = captureSafetyFingerprint;
    }

    public async Task<ApprovedImportPlan> BuildCurrentAsync(string analysisId, CancellationToken cancellationToken)
    {
        var stored = _analyses.GetRequired(analysisId);
        if (stored.FolderPath is not null)
            return await BuildUntrackedAsync(stored, analysisId, cancellationToken).ConfigureAwait(false);
        var queueMatches = _gateway.GetTrackedDownloads().Where(x =>
            string.Equals(x.DownloadId, stored.DownloadId, StringComparison.Ordinal) &&
            x.State is TrackedImportState.ImportBlocked or TrackedImportState.ImportFailed).ToArray();
        if (queueMatches.Length == 0)
            throw new StaleApprovalException("The failed import is no longer present in Lidarr.");

        var scan = _gateway.GetManualImportScan(stored.DownloadId);
        if (scan.IndeterminateCount != 0)
            throw new StaleApprovalException("Current Lidarr import evidence is contradictory; reconcile the Activity item and discover again.");
        var files = scan.Files;
        var proposal = stored.Proposal;
        if (proposal.Status != ProposalStatus.Proposed || proposal.ArtistId is not > 0 ||
            proposal.AlbumId is not > 0 || proposal.ReleaseId is not > 0 || files.Count == 0)
            throw new InvalidOperationException("Only a complete, validated import proposal may be approved.");
        // Discovery includes the selected queue row's album in its analysis snapshot. Rebuild
        // the exact same candidate scope here; using only file/proposal albums makes every
        // approval stale when Lidarr's queue album differs from the selected release album.
        var selectedQueueAlbumId = GetSelectedQueueAlbumId(queueMatches);
        var albumIds = FailedImportDiscoveryWorkflow.GetCandidateAlbumIds(files, selectedQueueAlbumId);
        var releases = albumIds.SelectMany(_gateway.GetReleaseCandidates)
            .GroupBy(x => x.Id).Select(x => x.First()).OrderBy(x => x.Id).ToArray();
        StrictProposalValidator.Validate(proposal, files, releases);
        var currentAnalysisId = FailedImportDiscoveryWorkflow.ComputeAnalysisId(
            stored.DownloadId, AiImportSourceKinds.Queue, selectedQueueAlbumId, files, releases, proposal);
        if (!string.Equals(currentAnalysisId, analysisId, StringComparison.Ordinal))
            throw new StaleApprovalException("The analysis no longer matches current Lidarr or source state.");

        var tracks = proposal.Tracks.ToDictionary(x => x.SourceFileId, StringComparer.Ordinal);
        var approved = new List<ApprovedImportFile>(files.Count);
        foreach (var source in files.OrderBy(x => x.Id, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!tracks.TryGetValue(source.Id, out var track) || track.TrackId <= 0 ||
                string.IsNullOrWhiteSpace(source.SourcePath) || !Path.IsPathFullyQualified(source.SourcePath) ||
                source.Quality is null || source.Quality.Count == 0 || string.IsNullOrWhiteSpace(source.DownloadId) ||
                source.Size < 0 || source.ManualImportId <= 0)
                throw new InvalidDataException("Lidarr did not provide complete execution material for an approved source file.");
            var identity = await _identities.InspectAsync(source.SourcePath, cancellationToken).ConfigureAwait(false);
            if (identity.Size != source.Size)
                throw new StaleApprovalException("A source file changed while its approval snapshot was being created.");
            approved.Add(new ApprovedImportFile(
                source.Id, source.ManualImportId, source.SourcePath, track.TrackId,
                (System.Text.Json.Nodes.JsonObject)source.Quality.DeepClone(), source.IndexerFlags,
                source.DownloadId, source.Size, identity));
        }

        var destinationRoot = await _execution.GetArtistPathAsync(proposal.ArtistId.Value, cancellationToken).ConfigureAwait(false);
        if (!Path.IsPathFullyQualified(destinationRoot) || Path.TrimEndingDirectorySeparator(destinationRoot) != destinationRoot)
            throw new InvalidDataException("Lidarr artist path must be an absolute path without a trailing separator.");
        var plan = new ApprovedImportPlan(
            analysisId, stored.DownloadId, proposal.ArtistId.Value, proposal.AlbumId.Value,
            proposal.ReleaseId.Value, _captureSafetyFingerprint(), approved, destinationRoot,
            stored.HumanOverride, stored.ParentAnalysisId, null, stored.AutomatedOverride);
        await _execution.RevalidateManualImportAsync(plan, cancellationToken).ConfigureAwait(false);
        return plan;
    }

    private async Task<ApprovedImportPlan> BuildUntrackedAsync(
        StoredStandaloneAnalysis stored,
        string analysisId,
        CancellationToken cancellationToken)
    {
        var folderPath = stored.FolderPath!;
        var scan = _gateway.GetUntrackedManualImportScan(folderPath);
        if (scan.IndeterminateCount != 0)
            throw new StaleApprovalException("Current Lidarr import evidence is contradictory; reconcile the Activity item and discover again.");
        var files = scan.Files;
        var proposal = stored.Proposal;
        if (proposal.Status != ProposalStatus.Proposed || proposal.ArtistId is not > 0 ||
            proposal.AlbumId is not > 0 || proposal.ReleaseId is not > 0 || files.Count == 0)
            throw new InvalidOperationException("Only a complete, validated import proposal may be approved.");
        // Untracked sources have no queue row; the candidate scope is exactly the albums
        // Lidarr's manual-import review already assigned to the source files.
        var albumIds = FailedImportDiscoveryWorkflow.GetCandidateAlbumIds(files, null);
        var releases = albumIds.SelectMany(_gateway.GetReleaseCandidates)
            .GroupBy(x => x.Id).Select(x => x.First()).OrderBy(x => x.Id).ToArray();
        StrictProposalValidator.Validate(proposal, files, releases);
        var currentAnalysisId = FailedImportDiscoveryWorkflow.ComputeAnalysisId(
            folderPath, AiImportSourceKinds.Folder, null, files, releases, proposal);
        if (!string.Equals(currentAnalysisId, analysisId, StringComparison.Ordinal))
            throw new StaleApprovalException("The analysis no longer matches current Lidarr or source state.");

        var tracks = proposal.Tracks.ToDictionary(x => x.SourceFileId, StringComparer.Ordinal);
        var approved = new List<ApprovedImportFile>(files.Count);
        foreach (var source in files.OrderBy(x => x.Id, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!tracks.TryGetValue(source.Id, out var track) || track.TrackId <= 0 ||
                string.IsNullOrWhiteSpace(source.SourcePath) || !Path.IsPathFullyQualified(source.SourcePath) ||
                source.Quality is null || source.Quality.Count == 0 ||
                source.Size < 0 || source.ManualImportId <= 0)
                throw new InvalidDataException("Lidarr did not provide complete execution material for an approved source file.");
            var identity = await _identities.InspectAsync(source.SourcePath, cancellationToken).ConfigureAwait(false);
            if (identity.Size != source.Size)
                throw new StaleApprovalException("A source file changed while its approval snapshot was being created.");
            approved.Add(new ApprovedImportFile(
                source.Id, source.ManualImportId, source.SourcePath, track.TrackId,
                (System.Text.Json.Nodes.JsonObject)source.Quality.DeepClone(), source.IndexerFlags,
                string.Empty, source.Size, identity));
        }

        var destinationRoot = await _execution.GetArtistPathAsync(proposal.ArtistId.Value, cancellationToken).ConfigureAwait(false);
        if (!Path.IsPathFullyQualified(destinationRoot) || Path.TrimEndingDirectorySeparator(destinationRoot) != destinationRoot)
            throw new InvalidDataException("Lidarr artist path must be an absolute path without a trailing separator.");
        var plan = new ApprovedImportPlan(
            analysisId, string.Empty, proposal.ArtistId.Value, proposal.AlbumId.Value,
            proposal.ReleaseId.Value, _captureSafetyFingerprint(), approved, destinationRoot,
            stored.HumanOverride, stored.ParentAnalysisId, folderPath, stored.AutomatedOverride);
        await _execution.RevalidateManualImportAsync(plan, cancellationToken).ConfigureAwait(false);
        return plan;
    }

    internal static int[] GetCandidateAlbumIds(
        IReadOnlyList<SourceFileCandidate> files,
        IReadOnlyList<QueueCandidate> queueMatches)
    {
        if (queueMatches.Count == 0) throw new ArgumentException("At least one matching queue row is required.", nameof(queueMatches));
        return FailedImportDiscoveryWorkflow.GetCandidateAlbumIds(files, GetSelectedQueueAlbumId(queueMatches));
    }

    private static int? GetSelectedQueueAlbumId(IReadOnlyList<QueueCandidate> queueMatches) =>
        queueMatches.OrderBy(x => x.Id).First().AlbumId;
}
