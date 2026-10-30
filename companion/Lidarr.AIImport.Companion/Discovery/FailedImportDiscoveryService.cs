using System.Security.Cryptography;
using System.Text.Json;
using Lidarr.AIImport.Companion.Lidarr;
using Lidarr.AIImport.Companion.Llm;
using Lidarr.AIImport.Companion.Matching;
using Lidarr.AIImport.Companion.Models;

namespace Lidarr.AIImport.Companion.Discovery;

public interface IFailedImportDiscoveryService
{
    Task<IReadOnlyList<AnalysisResult>> DiscoverAsync(CancellationToken cancellationToken);
}

public sealed class FailedImportDiscoveryService : IFailedImportDiscoveryService
{
    public const string CompatibleLidarrVersion = "3.1.2.4913";
    private static readonly HashSet<string> FailedImportStates =
        new(new[] { "importBlocked", "importFailed" }, StringComparer.OrdinalIgnoreCase);

    private readonly ILidarrClient _lidarrClient;
    private readonly DeterministicMatcher _matcher;
    private readonly ILlmMatchService? _llmMatchService;

    public FailedImportDiscoveryService(
        ILidarrClient lidarrClient,
        DeterministicMatcher matcher,
        ILlmMatchService? llmMatchService = null)
    {
        _lidarrClient = lidarrClient;
        _matcher = matcher;
        _llmMatchService = llmMatchService;
    }

    public async Task<IReadOnlyList<AnalysisResult>> DiscoverAsync(CancellationToken cancellationToken)
    {
        var status = await _lidarrClient.GetSystemStatusAsync(cancellationToken).ConfigureAwait(false);
        if (!string.Equals(status.Version, CompatibleLidarrVersion, StringComparison.Ordinal) ||
            !string.Equals(status.PackageVersion, CompatibleLidarrVersion, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Lidarr {CompatibleLidarrVersion} is required; connected version is {status.Version} (package {status.PackageVersion}).");
        }

        var queue = await _lidarrClient.GetQueueAsync(cancellationToken).ConfigureAwait(false);
        var failed = queue
            .Where(x => x.TrackedDownloadState is not null && FailedImportStates.Contains(x.TrackedDownloadState))
            .GroupBy(x => x.DownloadId, StringComparer.Ordinal)
            .Select(x => x.OrderBy(item => item.Id).First())
            .OrderBy(x => x.Id)
            .ThenBy(x => x.DownloadId, StringComparer.Ordinal)
            .ToArray();

        var results = new List<AnalysisResult>(failed.Length);
        foreach (var item in failed)
        {
            cancellationToken.ThrowIfCancellationRequested();
            results.Add(await AnalyzeAsync(item, cancellationToken).ConfigureAwait(false));
        }
        return results;
    }

    private async Task<AnalysisResult> AnalyzeAsync(QueueCandidate item, CancellationToken cancellationToken)
    {
        var files = await _lidarrClient.GetManualImportsAsync(item.DownloadId, null, cancellationToken).ConfigureAwait(false);
        var albumIds = files.Select(x => x.AlbumId)
            .Append(item.AlbumId)
            .Where(x => x.HasValue && x.Value > 0)
            .Select(x => x!.Value)
            .Distinct()
            .OrderBy(x => x)
            .ToArray();

        var candidates = new List<ReleaseCandidate>();
        foreach (var albumId in albumIds)
        {
            candidates.AddRange(await _lidarrClient.GetReleaseCandidatesAsync(albumId, cancellationToken).ConfigureAwait(false));
        }
        var verifiedCandidates = candidates
            .GroupBy(x => x.Id)
            .Select(x => x.First())
            .OrderBy(x => x.Id)
            .ToArray();

        var deterministic = _matcher.Match(files, verifiedCandidates);
        var proposal = ToProposal(deterministic, verifiedCandidates);
        var usedLlm = false;
        if (deterministic.Status == DeterministicStatus.NeedsLlm && _llmMatchService is not null)
        {
            usedLlm = true;
            try
            {
                proposal = await _llmMatchService.MatchAsync(
                    files,
                    verifiedCandidates,
                    deterministic.RankedReleaseIds,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is ProposalValidationException or HttpRequestException or LlmProtocolException or TaskCanceledException)
            {
                proposal = new MatchProposal(
                    1,
                    ProposalStatus.ManualReview,
                    null,
                    null,
                    null,
                    0m,
                    $"Local-model ranking failed safely: {exception.Message}",
                    Array.Empty<TrackProposal>());
            }
        }
        try
        {
            proposal = StrictProposalValidator.Validate(proposal, files, verifiedCandidates);
        }
        catch (ProposalValidationException exception)
        {
            proposal = new MatchProposal(
                1,
                ProposalStatus.ManualReview,
                null,
                null,
                null,
                0m,
                $"Deterministic proposal failed strict validation: {exception.Message}",
                Array.Empty<TrackProposal>());
        }
        var missingAlbum = BuildMissingAlbumProposal(files, verifiedCandidates);
        return new AnalysisResult(
            AnalysisId(item.DownloadId, files, verifiedCandidates, proposal, missingAlbum),
            item.DownloadId,
            item.Title,
            proposal,
            files,
            verifiedCandidates,
            UsedLlm: usedLlm,
            DateTimeOffset.UtcNow,
            missingAlbum);
    }

    private static MissingAlbumProposal? BuildMissingAlbumProposal(
        IReadOnlyList<SourceFileCandidate> files,
        IReadOnlyList<ReleaseCandidate> candidates)
    {
        if (candidates.Count != 0 || files.Count == 0) return null;
        var artistIds = files.Select(x => x.ArtistMusicBrainzId).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var albumIds = files.Select(x => x.AlbumMusicBrainzId).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var releaseIds = files.Select(x => x.ReleaseMusicBrainzId).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (artistIds.Length != 1 || albumIds.Length != 1 || releaseIds.Length != 1 ||
            !Guid.TryParse(artistIds[0], out var artistId) || artistId == Guid.Empty ||
            !Guid.TryParse(albumIds[0], out var albumId) || albumId == Guid.Empty ||
            !Guid.TryParse(releaseIds[0], out var releaseId) || releaseId == Guid.Empty)
            return null;
        var artists = files.Select(x => x.Artist).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.Ordinal).ToArray();
        var albums = files.Select(x => x.Album).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.Ordinal).ToArray();
        if (artists.Length != 1 || albums.Length != 1) return null;
        return new MissingAlbumProposal(artistId, albumId, releaseId, artists[0], albums[0]);
    }

    private static MatchProposal ToProposal(
        DeterministicResult result,
        IReadOnlyList<ReleaseCandidate> candidates)
    {
        if (result.Status == DeterministicStatus.Proposed && result.ReleaseId.HasValue)
        {
            var release = candidates.Single(x => x.Id == result.ReleaseId.Value);
            if (release.ArtistId.HasValue)
            {
                return new MatchProposal(
                    1,
                    ProposalStatus.Proposed,
                    release.ArtistId,
                    release.AlbumId,
                    release.Id,
                    result.Confidence,
                    result.Explanation,
                    result.Tracks);
            }
        }

        return new MatchProposal(
            1,
            ProposalStatus.ManualReview,
            null,
            null,
            null,
            result.Confidence,
            result.Explanation,
            Array.Empty<TrackProposal>());
    }

    private static string AnalysisId(
        string downloadId,
        IReadOnlyList<SourceFileCandidate> files,
        IReadOnlyList<ReleaseCandidate> candidates,
        MatchProposal proposal,
        MissingAlbumProposal? missingAlbum)
    {
        var snapshot = new
        {
            Version = 1,
            DownloadId = downloadId,
            Files = files.OrderBy(x => x.Id, StringComparer.Ordinal).Select(x => new
            {
                x.Id,
                x.Artist,
                x.Album,
                x.Title,
                x.DiscNumber,
                TrackNumbers = x.TrackNumbers.OrderBy(number => number).ToArray(),
                x.DurationMs,
                x.TrackMusicBrainzId,
                x.ArtistMusicBrainzId,
                x.AlbumMusicBrainzId,
                x.ReleaseMusicBrainzId,
                x.RecordingMusicBrainzId,
                x.SourcePath,
                Quality = x.Quality?.ToJsonString(),
                x.IndexerFlags,
                x.DownloadId,
                x.Size,
                x.ManualImportId,
                Rejections = x.Rejections.OrderBy(reason => reason, StringComparer.Ordinal).ToArray(),
                x.ArtistId,
                x.AlbumId
            }).ToArray(),
            Candidates = candidates.OrderBy(x => x.Id).Select(x => new
            {
                x.AlbumId,
                x.Id,
                x.Artist,
                x.Title,
                x.ForeignReleaseId,
                x.AlbumTitle,
                x.Disambiguation,
                x.ArtistId,
                Tracks = x.Tracks.OrderBy(track => track.Id).Select(track => new
                {
                    track.Id,
                    track.Title,
                    track.DiscNumber,
                    track.TrackNumber,
                    track.DurationMs,
                    track.ForeignTrackId,
                    track.ForeignRecordingId,
                    track.HasFile
                }).ToArray()
            }).ToArray(),
            Proposal = new
            {
                proposal.SchemaVersion,
                proposal.Status,
                proposal.ArtistId,
                proposal.AlbumId,
                proposal.ReleaseId,
                proposal.Confidence,
                proposal.Explanation,
                Tracks = proposal.Tracks.OrderBy(x => x.SourceFileId, StringComparer.Ordinal).Select(x => new
                {
                    x.SourceFileId,
                    x.TrackId,
                    x.Confidence
                }).ToArray()
            },
            MissingAlbum = missingAlbum
        };
        var hash = SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(snapshot));
        return Convert.ToHexString(hash).ToLowerInvariant()[..24];
    }
}
