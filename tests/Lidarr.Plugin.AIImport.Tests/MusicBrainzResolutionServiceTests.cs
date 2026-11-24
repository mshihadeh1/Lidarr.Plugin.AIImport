using System.Text.Json;
using Lidarr.Plugin.AIImport.Configuration;
using Lidarr.Plugin.AIImport.Discovery;
using Lidarr.Plugin.AIImport.Llm;
using Lidarr.Plugin.AIImport.MusicBrainz;

namespace Lidarr.Plugin.AIImport.Tests;

[TestFixture]
public sealed class MusicBrainzResolutionServiceTests
{
    [Test]
    public void Prefers_English_titles_without_requiring_exact_text_or_duration_equality()
    {
        var japaneseId = Guid.NewGuid().ToString("D");
        var englishId = Guid.NewGuid().ToString("D");
        var candidates = new[]
        {
            Candidate(japaneseId, "声", "声", 100, 247000),
            Candidate(englishId, "Voice", "Voice", 95, 247000)
        };
        var model = new CapturingModel(JsonSerializer.Serialize(new
        {
            status = "selected",
            releaseMusicBrainzId = englishId,
            confidence = 0.91m,
            explanation = "Same recording despite translated title and a small duration variance."
        }));
        var service = new MusicBrainzResolutionService(new FakeMusicBrainz(candidates), model);

        var result = service.Resolve(new AiImportSettings { PreferEnglishMusicBrainzTitles = true },
        [
            new SourceFileCandidate("source", "01 - 声.flac", "はてな", "声", "声?", 1, [1], 247306, null, [])
        ]);

        using var payload = JsonDocument.Parse(model.UserPrompt!);
        var releases = payload.RootElement.GetProperty("releaseCandidates").EnumerateArray().ToArray();
        Assert.Multiple(() =>
        {
            Assert.That(result.Result!.Addition.ReleaseMusicBrainzId, Is.EqualTo(englishId));
            Assert.That(releases[0].GetProperty("ReleaseMusicBrainzId").GetString(), Is.EqualTo(englishId));
            Assert.That(payload.RootElement.GetProperty("preferEnglishTitles").GetBoolean(), Is.True);
            Assert.That(model.SystemPrompt, Does.Contain("Minor punctuation"));
            Assert.That(model.SystemPrompt, Does.Contain("small duration differences"));
            Assert.That(model.SystemPrompt, Does.Not.Contain("exactly represents"));
        });
    }

    private static MusicBrainzReleaseCandidate Candidate(
        string releaseId, string releaseTitle, string trackTitle, int score, int duration) =>
        new(Guid.NewGuid().ToString("D"), Guid.NewGuid().ToString("D"), releaseId,
            "Artist", releaseTitle, "2021", "JP", null, score,
            [new MusicBrainzTrackCandidate(Guid.NewGuid().ToString("D"), trackTitle, 1, 1, duration)]);

    private sealed class FakeMusicBrainz(IReadOnlyList<MusicBrainzReleaseCandidate> candidates) : IMusicBrainzReleaseClient
    {
        public Task<MusicBrainzSearchResult> SearchAsync(IReadOnlyList<SourceFileCandidate> files, int limit, CancellationToken cancellationToken) =>
            Task.FromResult(new MusicBrainzSearchResult(candidates, "found candidates"));
        public Task<MusicBrainzReleaseCandidate?> GetAsync(Guid releaseId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class CapturingModel(string response) : IOpenAiChatClient
    {
        public string? SystemPrompt { get; private set; }
        public string? UserPrompt { get; private set; }
        public Task<LlmTestResult> TestConnectionAsync(AiImportSettings settings, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        public Task<string> CompleteJsonAsync(AiImportSettings settings, string systemPrompt, string userPrompt, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        public Task<string> CompleteMusicBrainzSelectionAsync(AiImportSettings settings, string systemPrompt, string userPrompt, CancellationToken cancellationToken)
        {
            SystemPrompt = systemPrompt;
            UserPrompt = userPrompt;
            return Task.FromResult(response);
        }
    }
}