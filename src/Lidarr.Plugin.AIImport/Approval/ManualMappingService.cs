using System.Text.Json;
using Lidarr.Plugin.AIImport.Discovery;
using Lidarr.Plugin.AIImport.Lidarr;
using Lidarr.Plugin.AIImport.Matching;
using Lidarr.Plugin.AIImport.Workflows;

namespace Lidarr.Plugin.AIImport.Approval;

public sealed record ManualMappingCreatedDto(string AnalysisId);

public sealed class ManualMappingService
{
    private static readonly HashSet<string> MappingFields = new(StringComparer.Ordinal)
    {
        "sourceFileId", "trackId"
    };
    private readonly StandaloneAnalysisStore _analyses;
    private readonly IInProcessLidarrGateway _gateway;
    private readonly TimeProvider _timeProvider;

    public ManualMappingService(StandaloneAnalysisStore analyses, IInProcessLidarrGateway gateway, TimeProvider? timeProvider = null)
    {
        _analyses = analyses;
        _gateway = gateway;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public ManualMappingCreatedDto Create(string analysisId, int releaseId, string mappingsJson, string confirmation)
    {
        if (!string.Equals(confirmation, "REVIEW", StringComparison.Ordinal))
            throw new ArgumentException("Exact manual-review confirmation REVIEW is required.", nameof(confirmation));
        if (releaseId <= 0) throw new ArgumentOutOfRangeException(nameof(releaseId));
        var stored = _analyses.GetRequired(analysisId);
        if (stored.Proposal.Status != ProposalStatus.ManualReview)
            throw new InvalidOperationException("Only a manual-review analysis can be replaced by a human-selected mapping.");
        var queueMatches = _gateway.GetTrackedDownloads().Where(x =>
            string.Equals(x.DownloadId, stored.DownloadId, StringComparison.Ordinal) &&
            x.State is TrackedImportState.ImportBlocked or TrackedImportState.ImportFailed).ToArray();
        if (queueMatches.Length == 0)
            throw new StaleApprovalException("The failed import is no longer present in Lidarr.");

        var queueAlbumId = queueMatches.OrderBy(x => x.Id).First().AlbumId;
        var files = _gateway.GetManualImports(stored.DownloadId);
        var albumIds = FailedImportDiscoveryWorkflow.GetCandidateAlbumIds(files, queueAlbumId);
        var releases = albumIds.SelectMany(_gateway.GetReleaseCandidates)
            .GroupBy(x => x.Id).Select(x => x.First()).OrderBy(x => x.Id).ToArray();
        var currentOriginalId = FailedImportDiscoveryWorkflow.ComputeAnalysisId(
            stored.DownloadId, queueAlbumId, files, releases, stored.Proposal);
        if (!string.Equals(currentOriginalId, analysisId, StringComparison.Ordinal))
            throw new StaleApprovalException("The analysis no longer matches current Lidarr or source state.");

        var release = releases.SingleOrDefault(x => x.Id == releaseId)
            ?? throw new ProposalValidationException("The selected release is not a current verified candidate.");
        if (!release.ArtistId.HasValue || release.ArtistId <= 0)
            throw new ProposalValidationException("The selected release has no verified artist owner.");
        var tracks = ParseMappings(mappingsJson);
        var proposal = StrictProposalValidator.Validate(new MatchProposal(
            1, ProposalStatus.Proposed, release.ArtistId, release.AlbumId, release.Id, 0m,
            "Explicit human-reviewed source-to-track mapping.", tracks), files, releases);
        var mappedAnalysisId = FailedImportDiscoveryWorkflow.ComputeAnalysisId(
            stored.DownloadId, queueAlbumId, files, releases, proposal);
        _analyses.Upsert(new StoredStandaloneAnalysis(
            mappedAnalysisId, stored.DownloadId, proposal, true, analysisId, _timeProvider.GetUtcNow().AddHours(1)));
        return new ManualMappingCreatedDto(mappedAnalysisId);
    }

    private static IReadOnlyList<TrackProposal> ParseMappings(string json)
    {
        if (string.IsNullOrWhiteSpace(json) || json.Length > 4_096 || Uri.EscapeDataString(json).Length > 6_000)
            throw new ArgumentException("A bounded mappings JSON array is required.", nameof(json));
        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow
            });
            if (document.RootElement.ValueKind != JsonValueKind.Array)
                throw new ProposalValidationException("Manual mappings must be a JSON array.");
            var result = new List<TrackProposal>();
            foreach (var item in document.RootElement.EnumerateArray())
            {
                if (result.Count >= 50)
                    throw new ProposalValidationException("Manual mappings exceed the supported file count.");
                if (item.ValueKind != JsonValueKind.Object)
                    throw new ProposalValidationException("Each manual mapping must be an object.");
                var names = item.EnumerateObject().Select(x => x.Name).ToArray();
                if (names.Distinct(StringComparer.Ordinal).Count() != names.Length ||
                    !names.ToHashSet(StringComparer.Ordinal).SetEquals(MappingFields))
                    throw new ProposalValidationException("Manual mapping fields are invalid.");
                var sourceProperty = item.GetProperty("sourceFileId");
                var sourceId = sourceProperty.ValueKind == JsonValueKind.String ? sourceProperty.GetString() : null;
                if (string.IsNullOrWhiteSpace(sourceId) || sourceId.Length > 200 ||
                    !item.GetProperty("trackId").TryGetInt32(out var trackId) || trackId <= 0)
                    throw new ProposalValidationException("Manual mapping identifiers are invalid.");
                result.Add(new TrackProposal(sourceId, trackId, 0m));
            }
            if (result.Count == 0)
                throw new ProposalValidationException("At least one manual mapping is required.");
            return result;
        }
        catch (JsonException)
        {
            throw new ProposalValidationException("Manual mappings are malformed JSON.");
        }
    }
}
