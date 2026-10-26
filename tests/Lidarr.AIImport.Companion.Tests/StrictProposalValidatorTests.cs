namespace Lidarr.AIImport.Companion.Tests;

[TestFixture]
public sealed class StrictProposalValidatorTests
{
    private static readonly SourceFileCandidate[] Files =
    {
        new("f1", "01.flac", "Artist", "Album", "Song", 1, new[] { 1 }, 180_000, null, Array.Empty<string>())
    };

    private static readonly ReleaseCandidate[] Releases =
    {
        new(10, 101, "Artist", "Album", "release-101", "Album", string.Empty,
            new[] { new TrackCandidate(1001, "Song", 1, "1", 180_000, null, "recording-1", false) }, 5)
    };

    [Test]
    public void Accepts_only_supplied_release_track_and_source_ids()
    {
        const string json = """{"schemaVersion":1,"status":"proposed","artistId":5,"albumId":10,"releaseId":101,"confidence":0.94,"explanation":"Best verified candidate","tracks":[{"sourceFileId":"f1","trackId":1001,"confidence":0.95}]}""";

        var result = StrictProposalValidator.ParseAndValidate(json, Files, Releases);

        Assert.That(result.ReleaseId, Is.EqualTo(101));
    }

    [TestCase("""{"schemaVersion":1,"status":"proposed","artistId":5,"albumId":10,"releaseId":999,"confidence":0.94,"explanation":"bad","tracks":[{"sourceFileId":"f1","trackId":1001,"confidence":0.95}]}""")]
    [TestCase("""{"schemaVersion":1,"status":"proposed","artistId":5,"albumId":10,"releaseId":101,"confidence":0.94,"explanation":"bad","tracks":[{"sourceFileId":"f1","trackId":9999,"confidence":0.95}]}""")]
    [TestCase("""{"schemaVersion":1,"status":"proposed","artistId":5,"albumId":10,"releaseId":101,"confidence":0.94,"explanation":"bad","tracks":[{"sourceFileId":"invented","trackId":1001,"confidence":0.95}]}""")]
    [TestCase("""{"schemaVersion":1,"status":"proposed","artistId":5,"albumId":10,"releaseId":101,"confidence":0.94,"explanation":"bad","tracks":[{"sourceFileId":"f1","trackId":1001,"confidence":0.95}],"extra":true}""")]
    public void Rejects_any_identifier_or_field_not_in_verified_input(string json)
    {
        Assert.Throws<ProposalValidationException>(() => StrictProposalValidator.ParseAndValidate(json, Files, Releases));
    }

    [Test]
    public void Manual_review_cannot_smuggle_track_mappings()
    {
        const string json = """{"schemaVersion":1,"status":"manualReview","artistId":null,"albumId":null,"releaseId":null,"confidence":0.2,"explanation":"uncertain","tracks":[{"sourceFileId":"f1","trackId":1001,"confidence":0.4}]}""";
        Assert.Throws<ProposalValidationException>(() => StrictProposalValidator.ParseAndValidate(json, Files, Releases));
    }

    [Test]
    public void Proposed_artist_must_match_verified_release_artist()
    {
        const string json = """{"schemaVersion":1,"status":"proposed","artistId":6,"albumId":10,"releaseId":101,"confidence":0.94,"explanation":"bad artist","tracks":[{"sourceFileId":"f1","trackId":1001,"confidence":0.95}]}""";

        Assert.Throws<ProposalValidationException>(() => StrictProposalValidator.ParseAndValidate(json, Files, Releases));
    }
}
