using Lidarr.Plugin.AIImport.Matching;

namespace Lidarr.Plugin.AIImport.Tests;

[TestFixture]
public sealed class StructuredMatchParserTests
{
    [Test]
    public void Parses_valid_strict_proposal()
    {
        var json = """
        {"schemaVersion":1,"status":"proposed","artistId":12,"albumId":34,"releaseId":56,"confidence":0.91,"explanation":"Verified candidate","tracks":[{"sourceFileId":"file-1","trackId":78,"confidence":0.95}]}
        """;

        var proposal = StructuredMatchParser.ParseAndValidate(json);

        Assert.That(proposal.ReleaseId, Is.EqualTo(56));
        Assert.That(proposal.Tracks, Has.Count.EqualTo(1));
    }

    [Test]
    public void Supports_markdown_fence_fallback_for_models_without_json_mode()
    {
        var json = """
        ```json
        {"schemaVersion":1,"status":"manualReview","artistId":null,"albumId":null,"releaseId":null,"confidence":0.2,"explanation":"Ambiguous","tracks":[]}
        ```
        """;

        var proposal = StructuredMatchParser.ParseAndValidate(json);

        Assert.That(proposal.Status, Is.EqualTo(ProposalStatus.ManualReview));
    }

    [TestCase("{\"schemaVersion\":1,\"status\":\"proposed\",\"artistId\":1,\"albumId\":2,\"releaseId\":3,\"confidence\":1.1,\"explanation\":\"bad\",\"tracks\":[]}")]
    [TestCase("{\"schemaVersion\":1,\"status\":\"proposed\",\"artistId\":1,\"albumId\":2,\"releaseId\":3,\"confidence\":0.9,\"explanation\":\"bad\",\"tracks\":[{\"sourceFileId\":\"file-1\",\"trackId\":null,\"confidence\":0.9}]}")]
    [TestCase("{\"schemaVersion\":1,\"status\":\"invented\",\"artistId\":1,\"albumId\":2,\"releaseId\":3,\"confidence\":0.9,\"explanation\":\"bad\",\"tracks\":[]}")]
    public void Rejects_schema_violations(string json)
    {
        Assert.Throws<MatchSchemaException>(() => StructuredMatchParser.ParseAndValidate(json));
    }

    [Test]
    public void Rejects_unknown_properties()
    {
        var json = """{"schemaVersion":1,"status":"manualReview","artistId":null,"albumId":null,"releaseId":null,"confidence":0.2,"explanation":"Ambiguous","tracks":[],"surprise":true}""";
        Assert.Throws<MatchSchemaException>(() => StructuredMatchParser.ParseAndValidate(json));
    }

    [Test]
    public void Rejects_prose_outside_markdown_fence()
    {
        var json = """
        ```json
        {"schemaVersion":1,"status":"manualReview","artistId":null,"albumId":null,"releaseId":null,"confidence":0.2,"explanation":"Ambiguous","tracks":[]}
        ```
        I hope this helps.
        """;

        Assert.Throws<MatchSchemaException>(() => StructuredMatchParser.ParseAndValidate(json));
    }

    [Test]
    public void Rejects_duplicate_json_properties()
    {
        var json = """{"schemaVersion":1,"status":"manualReview","status":"proposed","artistId":1,"albumId":2,"releaseId":3,"confidence":0.9,"explanation":"Ambiguous","tracks":[{"sourceFileId":"file-1","trackId":4,"confidence":0.9}]}""";

        Assert.Throws<MatchSchemaException>(() => StructuredMatchParser.ParseAndValidate(json));
    }
}
