using System.Text.Json;
using Lidarr.Plugin.AIImport.Configuration;
using Lidarr.Plugin.AIImport.Discovery;
using Lidarr.Plugin.AIImport.Llm;

namespace Lidarr.Plugin.AIImport.MusicBrainz;

public sealed record MusicBrainzResolutionResult(
    LibraryAdditionDto Addition,
    decimal Confidence,
    string Explanation,
    int CandidateCount);

public interface IMusicBrainzResolutionService
{
    MusicBrainzResolutionResult? Resolve(
        AiImportSettings settings,
        IReadOnlyList<SourceFileCandidate> files);
}

public sealed class MusicBrainzResolutionService : IMusicBrainzResolutionService
{
    private const decimal MinimumAutomaticConfidence = 0.85m;
    private const string SystemPrompt = """
        Select the single MusicBrainz release that exactly represents this complete downloaded album.
        Compare artist, release title, disc/track positions, track titles, track count, and durations.
        Select only a supplied releaseMusicBrainzId. Use notFound when more than one release remains plausible,
        when the source appears incomplete, or when no candidate has a complete one-to-one track correspondence.
        Confidence is between 0 and 1. Return only the required JSON object.
        """;

    private readonly IMusicBrainzReleaseClient _musicBrainz;
    private readonly IOpenAiChatClient _llm;

    public MusicBrainzResolutionService(IMusicBrainzReleaseClient musicBrainz, IOpenAiChatClient llm)
    {
        _musicBrainz = musicBrainz;
        _llm = llm;
    }

    public MusicBrainzResolutionResult? Resolve(
        AiImportSettings settings,
        IReadOnlyList<SourceFileCandidate> files)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(files);
        if (files.Count == 0) return null;

        var searched = _musicBrainz.SearchAsync(files, settings.MusicBrainzCandidateLimit, CancellationToken.None)
            .GetAwaiter().GetResult();
        // YOLO imports complete albums. Excluding different track counts before the model call
        // prevents a plausible title match from authorizing a partial or expanded edition.
        var candidates = searched
            .Where(x => x.Tracks.Count == files.Count)
            .OrderByDescending(x => x.SearchScore)
            .ThenBy(x => x.ReleaseMusicBrainzId, StringComparer.Ordinal)
            .ToArray();
        if (candidates.Length == 0) return null;

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
                x.RecordingMusicBrainzId
            }),
            releaseCandidates = candidates.Select(x => new
            {
                x.ReleaseMusicBrainzId,
                x.ReleaseGroupMusicBrainzId,
                x.ArtistMusicBrainzId,
                x.Artist,
                x.Title,
                x.Date,
                x.Country,
                x.Disambiguation,
                x.SearchScore,
                tracks = x.Tracks.OrderBy(t => t.DiscNumber).ThenBy(t => t.TrackNumber).Select(t => new
                {
                    t.RecordingMusicBrainzId,
                    t.Title,
                    t.DiscNumber,
                    t.TrackNumber,
                    t.DurationMs
                })
            })
        };

        var output = _llm.CompleteMusicBrainzSelectionAsync(
            settings,
            SystemPrompt,
            JsonSerializer.Serialize(payload),
            CancellationToken.None).GetAwaiter().GetResult();
        var selection = ParseSelection(output);
        if (!string.Equals(selection.Status, "selected", StringComparison.Ordinal) ||
            selection.ReleaseMusicBrainzId is null || selection.Confidence < MinimumAutomaticConfidence)
            return null;

        var selected = candidates.SingleOrDefault(x =>
            string.Equals(x.ReleaseMusicBrainzId, selection.ReleaseMusicBrainzId, StringComparison.OrdinalIgnoreCase));
        if (selected is null)
            throw new LlmProtocolException("The model selected a MusicBrainz release that was not supplied.");

        return new MusicBrainzResolutionResult(
            new LibraryAdditionDto(
                selected.ArtistMusicBrainzId,
                selected.ReleaseGroupMusicBrainzId,
                selected.ReleaseMusicBrainzId,
                selected.Artist,
                selected.Title),
            selection.Confidence,
            selection.Explanation,
            candidates.Length);
    }

    internal static MusicBrainzSelection ParseSelection(string output)
    {
        try
        {
            using var document = JsonDocument.Parse(output);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw new LlmProtocolException("MusicBrainz selection was not an object.");
            var expected = new HashSet<string>(StringComparer.Ordinal)
            {
                "status", "releaseMusicBrainzId", "confidence", "explanation"
            };
            var actual = root.EnumerateObject().Select(x => x.Name).ToArray();
            if (actual.Length != expected.Count || actual.Any(x => !expected.Contains(x)))
                throw new LlmProtocolException("MusicBrainz selection contained unknown or missing properties.");

            var status = root.GetProperty("status").GetString();
            var releaseId = root.GetProperty("releaseMusicBrainzId").ValueKind == JsonValueKind.Null
                ? null
                : root.GetProperty("releaseMusicBrainzId").GetString();
            var confidence = root.GetProperty("confidence").GetDecimal();
            var explanation = root.GetProperty("explanation").GetString();
            if (status is not ("selected" or "notFound") || confidence is < 0m or > 1m ||
                string.IsNullOrWhiteSpace(explanation) || status == "notFound" && releaseId is not null)
                throw new LlmProtocolException("MusicBrainz selection values were invalid.");
            if (status == "selected" && (!Guid.TryParse(releaseId, out var selectedId) || selectedId == Guid.Empty))
                throw new LlmProtocolException("MusicBrainz selection values were invalid.");
            return new MusicBrainzSelection(status, releaseId, confidence, explanation!);
        }
        catch (LlmProtocolException)
        {
            throw;
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or KeyNotFoundException or FormatException)
        {
            throw new LlmProtocolException("The model returned a malformed MusicBrainz selection.");
        }
    }
}

public sealed record MusicBrainzSelection(
    string Status,
    string? ReleaseMusicBrainzId,
    decimal Confidence,
    string Explanation);
