using Lidarr.AIImport.Companion.Llm;
using Lidarr.AIImport.Companion.Matching;

namespace Lidarr.AIImport.Companion.Tests;

[TestFixture]
public sealed class LlmMatchServiceTests
{
    [Test]
    public async Task Accepts_only_an_exhaustive_verified_candidate_mapping()
    {
        var llm = new FakeLlmClient("""
            {"schemaVersion":1,"status":"proposed","artistId":5,"albumId":10,"releaseId":101,"confidence":0.93,"explanation":"Matched tags to candidate","tracks":[{"sourceFileId":"source-1","trackId":1001,"confidence":0.95}]}
            """);
        var service = new LlmMatchService(llm);

        var result = await service.MatchAsync([Source()], [Release()], [101], CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(result.ReleaseId, Is.EqualTo(101));
            Assert.That(llm.UserPrompt, Does.Contain("source-1"));
            Assert.That(llm.UserPrompt, Does.Contain("1001"));
            Assert.That(llm.UserPrompt, Does.Not.Contain("/downloads/"));
        });
    }

    [Test]
    public void Rejects_an_invented_track_id()
    {
        var llm = new FakeLlmClient("""
            {"schemaVersion":1,"status":"proposed","artistId":5,"albumId":10,"releaseId":101,"confidence":0.93,"explanation":"invented","tracks":[{"sourceFileId":"source-1","trackId":9999,"confidence":0.95}]}
            """);

        Assert.ThrowsAsync<ProposalValidationException>(() =>
            new LlmMatchService(llm).MatchAsync([Source()], [Release()], [101], CancellationToken.None));
    }

    private static SourceFileCandidate Source() =>
        new("source-1", "01 - Song.flac", "Artist", "Album", "Song", 1, [1], 180000, null, [], 5, 10);

    private static ReleaseCandidate Release() =>
        new(10, 101, "Artist", "Release", "mb-release", "Album", string.Empty,
            [new TrackCandidate(1001, "Song", 1, "1", 180000, "mb-track", "mb-recording", false)], 5);

    private sealed class FakeLlmClient(string output) : IOpenAiChatClient
    {
        public string? UserPrompt { get; private set; }

        public Task<string> CompleteJsonAsync(string systemPrompt, string userPrompt, CancellationToken cancellationToken)
        {
            UserPrompt = userPrompt;
            return Task.FromResult(output);
        }
    }
}
