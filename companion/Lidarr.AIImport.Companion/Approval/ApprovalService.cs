using System.Text.Json;
using System.Text.Json.Nodes;
using Lidarr.AIImport.Companion.Discovery;
using Lidarr.AIImport.Companion.Filesystem;
using Lidarr.AIImport.Companion.Lidarr;
using Lidarr.AIImport.Companion.Models;
using Lidarr.AIImport.Companion.Safety;

namespace Lidarr.AIImport.Companion.Approval;

public sealed record ApprovedImportFile(
    string SourceId,
    int ManualImportId,
    string SourcePath,
    int TrackId,
    JsonObject Quality,
    int IndexerFlags,
    string DownloadId,
    long Size,
    FileIdentity Identity);

public sealed record ApprovedImportPlan(
    string AnalysisId,
    string DownloadId,
    int ArtistId,
    int AlbumId,
    int ReleaseId,
    string SafetyFingerprint,
    IReadOnlyList<ApprovedImportFile> Files,
    string DestinationRoot = "");

public interface IApprovedPlanRevalidator
{
    Task RevalidateAsync(ApprovedImportPlan expected, CancellationToken cancellationToken);
}

public sealed class ApprovalService : IApprovedPlanRevalidator
{
    private readonly IFailedImportDiscoveryService _discovery;
    private readonly IFileIdentityProvider _identities;
    private readonly ApprovalStore _store;
    private readonly IImportSafetySnapshotProvider _safety;
    private readonly ILidarrExecutionClient _executionClient;

    public ApprovalService(
        IFailedImportDiscoveryService discovery,
        IFileIdentityProvider identities,
        ApprovalStore store,
        IImportSafetySnapshotProvider safety,
        ILidarrExecutionClient executionClient)
    {
        _discovery = discovery;
        _identities = identities;
        _store = store;
        _safety = safety;
        _executionClient = executionClient;
    }

    public async Task<CreatedApproval> ApproveAsync(
        string analysisId,
        TimeSpan lifetime,
        CancellationToken cancellationToken)
    {
        var plan = await BuildCurrentPlanAsync(analysisId, cancellationToken).ConfigureAwait(false);
        return await _store.CreateAsync(
            plan.AnalysisId,
            JsonSerializer.SerializeToElement(plan),
            lifetime,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task RevalidateAsync(ApprovedImportPlan expected, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(expected);
        var current = await BuildCurrentPlanAsync(expected.AnalysisId, cancellationToken).ConfigureAwait(false);
        var expectedJson = JsonSerializer.Serialize(expected);
        var currentJson = JsonSerializer.Serialize(current);
        if (!string.Equals(expectedJson, currentJson, StringComparison.Ordinal))
            throw new StaleApprovalException("The approved import plan no longer matches current Lidarr, safety, or source state.");
    }

    private async Task<ApprovedImportPlan> BuildCurrentPlanAsync(string analysisId, CancellationToken cancellationToken)
    {
        var matches = (await _discovery.DiscoverAsync(cancellationToken).ConfigureAwait(false))
            .Where(x => string.Equals(x.AnalysisId, analysisId, StringComparison.Ordinal))
            .ToArray();
        if (matches.Length != 1)
            throw new StaleApprovalException("The analysis is missing or stale; refresh discovery before approval.");
        var analysis = matches[0];
        var proposal = analysis.Proposal;
        if (proposal.Status != ProposalStatus.Proposed ||
            proposal.ArtistId is not > 0 || proposal.AlbumId is not > 0 || proposal.ReleaseId is not > 0)
            throw new InvalidOperationException("Only a complete, validated import proposal may be approved.");
        if (analysis.Files.Count == 0 || proposal.Tracks.Count != analysis.Files.Count)
            throw new InvalidOperationException("The proposal does not exhaustively map every source file.");

        var trackBySource = proposal.Tracks.ToDictionary(x => x.SourceFileId, StringComparer.Ordinal);
        var approvedFiles = new List<ApprovedImportFile>(analysis.Files.Count);
        foreach (var source in analysis.Files.OrderBy(x => x.Id, StringComparer.Ordinal))
        {
            if (!trackBySource.TryGetValue(source.Id, out var track) || track.TrackId <= 0)
                throw new InvalidOperationException("The proposal contains an invalid source-to-track mapping.");
            if (string.IsNullOrWhiteSpace(source.SourcePath) || !Path.IsPathFullyQualified(source.SourcePath) ||
                source.Quality is null || string.IsNullOrWhiteSpace(source.DownloadId) || source.Size < 0 ||
                source.ManualImportId <= 0)
                throw new InvalidOperationException("Lidarr did not provide complete execution material for a source file.");
            var identity = await _identities.InspectAsync(source.SourcePath, cancellationToken).ConfigureAwait(false);
            if (identity.Size != source.Size)
                throw new StaleApprovalException("A source file changed while its approval snapshot was being created.");
            approvedFiles.Add(new ApprovedImportFile(
                source.Id,
                source.ManualImportId,
                source.SourcePath,
                track.TrackId,
                (JsonObject)source.Quality.DeepClone(),
                source.IndexerFlags,
                source.DownloadId,
                source.Size,
                identity));
        }

        var safety = await _safety.CaptureAsync(cancellationToken).ConfigureAwait(false);
        var destinationRoot = await _executionClient.GetArtistPathAsync(
            proposal.ArtistId.Value, cancellationToken).ConfigureAwait(false);
        if (!Path.IsPathFullyQualified(destinationRoot))
            throw new InvalidDataException("Lidarr artist path is not an absolute destination root.");
        var plan = new ApprovedImportPlan(
            analysis.AnalysisId,
            analysis.DownloadId,
            proposal.ArtistId.Value,
            proposal.AlbumId.Value,
            proposal.ReleaseId.Value,
            safety.Fingerprint,
            approvedFiles,
            destinationRoot);
        await _executionClient.RevalidateManualImportAsync(plan, cancellationToken).ConfigureAwait(false);
        return plan;
    }
}
