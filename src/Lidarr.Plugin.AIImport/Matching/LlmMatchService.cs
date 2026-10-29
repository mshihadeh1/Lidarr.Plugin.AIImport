using System.Text.Json;
using Lidarr.Plugin.AIImport.Configuration;
using Lidarr.Plugin.AIImport.Discovery;
using Lidarr.Plugin.AIImport.Llm;

namespace Lidarr.Plugin.AIImport.Matching;

public interface ILlmMatchService
{
    MatchProposal Match(
        AiImportSettings settings,
        IReadOnlyList<SourceFileCandidate> files,
        IReadOnlyList<ReleaseCandidate> candidates,
        IReadOnlyList<int> rankedReleaseIds);
}

public sealed class LlmMatchService : ILlmMatchService
{
    private const string SystemPrompt = """
        Match every source file to exactly one track from exactly one supplied release candidate.
        Use tagged artist, album, title, disc, track number, duration, and MusicBrainz identifiers as evidence.
        Return only JSON with exactly these root properties:
        schemaVersion, status, artistId, albumId, releaseId, confidence, explanation, tracks.
        status is proposed or manualReview. A proposed result maps every sourceFileId exactly once to a unique available trackId.
        A manualReview result uses null IDs and an empty tracks array.
        """;

    private readonly IOpenAiChatClient _client;

    public LlmMatchService(IOpenAiChatClient client)
    {
        _client = client;
    }

    public MatchProposal Match(
        AiImportSettings settings,
        IReadOnlyList<SourceFileCandidate> files,
        IReadOnlyList<ReleaseCandidate> candidates,
        IReadOnlyList<int> rankedReleaseIds)
    {
        var ranked = rankedReleaseIds.Distinct().ToArray();
        var verified = candidates.Where(x => ranked.Contains(x.Id)).OrderBy(x => Array.IndexOf(ranked, x.Id)).ToArray();
        if (verified.Length == 0 || verified.Length != ranked.Length)
        {
            throw new ProposalValidationException("LLM candidate ranking referenced an unavailable release.");
        }

        var payload = new
        {
            sourceFiles = files.OrderBy(x => x.Id, StringComparer.Ordinal).Select(x => new
            {
                sourceFileId = x.Id,
                x.Name,
                x.Artist,
                x.Album,
                x.Title,
                x.DiscNumber,
                x.TrackNumbers,
                x.DurationMs,
                x.TrackMusicBrainzId,
                x.ArtistMusicBrainzId,
                x.AlbumMusicBrainzId,
                x.ReleaseMusicBrainzId,
                x.RecordingMusicBrainzId
            }),
            releaseCandidates = verified.Select(x => new
            {
                artistId = x.ArtistId,
                albumId = x.AlbumId,
                releaseId = x.Id,
                x.Artist,
                releaseTitle = x.Title,
                x.AlbumTitle,
                x.Disambiguation,
                x.ForeignReleaseId,
                tracks = x.Tracks.Where(track => !track.HasFile).OrderBy(track => track.Id).Select(track => new
                {
                    trackId = track.Id,
                    track.Title,
                    track.DiscNumber,
                    track.TrackNumber,
                    track.DurationMs,
                    track.ForeignTrackId,
                    track.ForeignRecordingId
                })
            })
        };

        var output = _client.CompleteJsonAsync(
            settings,
            SystemPrompt,
            JsonSerializer.Serialize(payload),
            CancellationToken.None).GetAwaiter().GetResult();
        return StrictProposalValidator.ParseAndValidate(output, files, verified);
    }
}
