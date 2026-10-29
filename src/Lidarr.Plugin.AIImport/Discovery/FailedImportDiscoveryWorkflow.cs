using System.Security.Cryptography;
using System.Text.Json;
using Lidarr.Plugin.AIImport.Configuration;
using Lidarr.Plugin.AIImport.Lidarr;
using Lidarr.Plugin.AIImport.Llm;
using Lidarr.Plugin.AIImport.Matching;
using Lidarr.Plugin.AIImport.Workflows;

namespace Lidarr.Plugin.AIImport.Discovery;

public sealed class FailedImportDiscoveryWorkflow : IAiImportDiscoverWorkflow
{
    public const string CompatibleLidarrVersion = "3.1.2.4913";

    private readonly IInProcessLidarrGateway _gateway;
    private readonly DeterministicMatcher _matcher;
    private readonly ILlmMatchService? _llm;
    private readonly StandaloneAnalysisStore? _analysisStore;

    public FailedImportDiscoveryWorkflow(
        IInProcessLidarrGateway gateway,
        DeterministicMatcher matcher,
        ILlmMatchService? llm)
    {
        _gateway = gateway;
        _matcher = matcher;
        _llm = llm;
    }

    public FailedImportDiscoveryWorkflow(
        IInProcessLidarrGateway gateway,
        DeterministicMatcher matcher,
        ILlmMatchService? llm,
        StandaloneAnalysisStore analysisStore)
        : this(gateway, matcher, llm)
    {
        _analysisStore = analysisStore;
    }

    public AiImportDiscoverResult Discover(AiImportSettings settings)
    {
        if (!string.Equals(_gateway.Version, CompatibleLidarrVersion, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Lidarr {CompatibleLidarrVersion} is required; running version is {_gateway.Version}.");
        }

        var failed = _gateway.GetTrackedDownloads()
            .Where(x => x.State is TrackedImportState.ImportBlocked or TrackedImportState.ImportFailed)
            .GroupBy(x => x.DownloadId, StringComparer.Ordinal)
            .Select(x => x.OrderBy(item => item.Id).First())
            .OrderBy(x => x.Id)
            .ThenBy(x => x.DownloadId, StringComparer.Ordinal)
            .ToArray();

        var analyses = new List<AiImportAnalysisDto>();
        foreach (var item in failed)
        {
            try
            {
                analyses.Add(Analyze(item, settings));
            }
            catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
            {
                // A download may disappear between the tracked-download snapshot and its manual-import scan.
                // Skip only that stale item; never let it hide other independently reviewable candidates.
            }
        }
        _analysisStore?.Replace(analyses.Select(x =>
            new StoredStandaloneAnalysis(x.AnalysisId, x.DownloadId, x.Proposal)).ToArray());
        return new AiImportDiscoverResult(analyses.ToArray());
    }

    private AiImportAnalysisDto Analyze(QueueCandidate item, AiImportSettings settings)
    {
        var files = _gateway.GetManualImports(item.DownloadId);
        var albumIds = files.Select(x => x.AlbumId)
            .Append(item.AlbumId)
            .Where(x => x is > 0)
            .Select(x => x!.Value)
            .Distinct()
            .OrderBy(x => x)
            .ToArray();
        var candidates = albumIds.SelectMany(_gateway.GetReleaseCandidates)
            .GroupBy(x => x.Id)
            .Select(x => x.First())
            .OrderBy(x => x.Id)
            .ToArray();

        var deterministic = _matcher.Match(files, candidates);
        var proposal = ToProposal(deterministic, candidates);
        var usedLlm = false;
        if (deterministic.Status == DeterministicStatus.NeedsLlm && _llm is not null)
        {
            usedLlm = true;
            try
            {
                proposal = _llm.Match(settings, files, candidates, deterministic.RankedReleaseIds);
            }
            catch (Exception exception) when (exception is ProposalValidationException or HttpRequestException or LlmProtocolException or TaskCanceledException)
            {
                proposal = ManualReview($"Local-model ranking failed safely: {exception.Message}");
            }
        }

        try
        {
            proposal = StrictProposalValidator.Validate(proposal, files, candidates);
        }
        catch (ProposalValidationException exception)
        {
            proposal = ManualReview($"Proposal failed strict validation: {exception.Message}");
        }

        var safeFiles = files.Select(x => new SourceFileDto(
            x.Id, x.Name, x.Artist, x.Album, x.Title, x.DiscNumber, x.TrackNumbers.ToArray(), x.DurationMs)).ToArray();
        var safeCandidates = candidates.Select(x => new ReleaseCandidateDto(
            x.AlbumId, x.Id, x.ArtistId, x.Artist, x.Title, x.ForeignReleaseId)).ToArray();
        return new AiImportAnalysisDto(
            ComputeAnalysisId(item.DownloadId, files, candidates, proposal),
            item.DownloadId,
            item.Title,
            proposal,
            safeFiles,
            safeCandidates,
            usedLlm);
    }

    private static MatchProposal ToProposal(DeterministicResult result, IReadOnlyList<ReleaseCandidate> candidates)
    {
        if (result.Status == DeterministicStatus.Proposed && result.ReleaseId.HasValue)
        {
            var release = candidates.Single(x => x.Id == result.ReleaseId.Value);
            if (release.ArtistId.HasValue)
            {
                return new MatchProposal(
                    1, ProposalStatus.Proposed, release.ArtistId, release.AlbumId, release.Id,
                    result.Confidence, result.Explanation, result.Tracks);
            }
        }

        return ManualReview(result.Explanation, result.Confidence);
    }

    private static MatchProposal ManualReview(string explanation, decimal confidence = 0m) =>
        new(1, ProposalStatus.ManualReview, null, null, null, confidence, explanation, Array.Empty<TrackProposal>());

    public static string ComputeAnalysisId(
        string downloadId,
        IReadOnlyList<SourceFileCandidate> files,
        IReadOnlyList<ReleaseCandidate> candidates,
        MatchProposal proposal)
    {
        var snapshot = new
        {
            version = 1,
            downloadId,
            files = files.OrderBy(x => x.Id, StringComparer.Ordinal).Select(x => new
            {
                x.Id, x.Artist, x.Album, x.Title, x.DiscNumber, x.TrackNumbers, x.DurationMs,
                x.TrackMusicBrainzId, x.ArtistId, x.AlbumId, x.SourcePath,
                rejections = x.Rejections.OrderBy(value => value, StringComparer.Ordinal)
            }),
            candidates = candidates.OrderBy(x => x.Id),
            proposal
        };
        var hash = SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(snapshot));
        return Convert.ToHexString(hash).ToLowerInvariant()[..24];
    }
}
