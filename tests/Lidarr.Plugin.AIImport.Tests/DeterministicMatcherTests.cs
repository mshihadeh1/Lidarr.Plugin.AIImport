using Lidarr.Plugin.AIImport.Discovery;
using Lidarr.Plugin.AIImport.Matching;

namespace Lidarr.Plugin.AIImport.Tests;

[TestFixture]
public sealed class DeterministicMatcherTests
{
    [Test]
    public void Exact_number_title_and_duration_match_is_auto_proposed()
    {
        var files = new[]
        {
            Source("f1", "First Song", 1, 1, 181_000),
            Source("f2", "Second Song", 1, 2, 202_000)
        };
        var release = Release(101, "Album", new[]
        {
            Track(1001, "First Song", 1, "1", 180_000),
            Track(1002, "Second Song", 1, "2", 201_000)
        });

        var result = new DeterministicMatcher().Match(files, new[] { release });

        Assert.Multiple(() =>
        {
            Assert.That(result.Status, Is.EqualTo(DeterministicStatus.Proposed));
            Assert.That(result.ReleaseId, Is.EqualTo(101));
            Assert.That(result.Tracks, Has.Count.EqualTo(2));
            Assert.That(result.Confidence, Is.GreaterThanOrEqualTo(0.92m));
        });
    }

    [Test]
    public void Embedded_musicbrainz_track_id_has_priority()
    {
        var files = new[] { Source("f1", "wrong title", 1, 9, 999_000, "mb-track-2") };
        var release = Release(101, "Album", new[]
        {
            Track(1001, "First Song", 1, "1", 180_000, "mb-track-1"),
            Track(1002, "Second Song", 1, "2", 201_000, "mb-track-2")
        });

        var result = new DeterministicMatcher().Match(files, new[] { release });

        Assert.That(result.Tracks.Single().TrackId, Is.EqualTo(1002));
    }

    [Test]
    public void Near_tied_deluxe_releases_require_llm_review()
    {
        var files = new[] { Source("f1", "First Song", 1, 1, 181_000) };
        var standard = Release(101, "Album", new[] { Track(1001, "First Song", 1, "1", 180_000) });
        var deluxe = Release(102, "Album (Deluxe)", new[] { Track(2001, "First Song", 1, "1", 180_000) });

        var result = new DeterministicMatcher().Match(files, new[] { standard, deluxe });

        Assert.That(result.Status, Is.EqualTo(DeterministicStatus.NeedsLlm));
    }

    [Test]
    public void Existing_library_track_is_not_selected()
    {
        var files = new[] { Source("f1", "First Song", 1, 1, 181_000) };
        var release = Release(101, "Album", new[] { Track(1001, "First Song", 1, "1", 180_000, hasFile: true) });

        var result = new DeterministicMatcher().Match(files, new[] { release });

        Assert.That(result.Status, Is.EqualTo(DeterministicStatus.ManualReview));
    }

    [Test]
    public void Finds_complete_one_to_one_mapping_instead_of_greedy_partial_mapping()
    {
        var files = new[]
        {
            Source("f1", "Alpha Beta", 1, 1, 0),
            Source("f2", "Alpha", 1, 1, 0)
        };
        var release = Release(101, "Album", new[]
        {
            Track(1001, "Alpha Beta", 1, "1", 0),
            Track(1002, "Alpha Beta Gamma", 1, "1", 0)
        });

        var result = new DeterministicMatcher().Match(files, new[] { release });

        Assert.Multiple(() =>
        {
            Assert.That(result.Status, Is.EqualTo(DeterministicStatus.NeedsLlm));
            Assert.That(result.RankedReleaseIds, Does.Contain(101));
        });
    }

    [Test]
    public void Authoritative_source_album_or_artist_mismatch_cannot_be_proposed()
    {
        var files = new[]
        {
            Source("f1", "First Song", 1, 1, 180_000) with { ArtistId = 6, AlbumId = 11 }
        };
        var release = Release(101, "Album", new[] { Track(1001, "First Song", 1, "1", 180_000) }) with { ArtistId = 5 };

        var result = new DeterministicMatcher().Match(files, new[] { release });

        Assert.That(result.Status, Is.EqualTo(DeterministicStatus.ManualReview));
    }

    [Test]
    public void Mixed_source_album_text_cannot_be_auto_proposed_from_first_file_only()
    {
        var files = new[]
        {
            Source("f1", "First Song", 1, 1, 180_000),
            Source("f2", "Second Song", 1, 2, 180_000) with { Album = "Different Album" }
        };
        var release = Release(101, "Album", new[]
        {
            Track(1001, "First Song", 1, "1", 180_000),
            Track(1002, "Second Song", 1, "2", 180_000)
        });

        var result = new DeterministicMatcher().Match(files, new[] { release });

        Assert.That(result.Status, Is.EqualTo(DeterministicStatus.NeedsLlm));
    }

    private static SourceFileCandidate Source(string id, string title, int disc, int track, int durationMs, string? mbid = null) =>
        new(id, $"{track:00} - {title}.flac", "Artist", "Album", title, disc, new[] { track }, durationMs, mbid, Array.Empty<string>());

    private static ReleaseCandidate Release(int id, string title, IReadOnlyList<TrackCandidate> tracks) =>
        new(10, id, "Artist", title, $"release-{id}", "Album", string.Empty, tracks);

    private static TrackCandidate Track(int id, string title, int disc, string number, int durationMs, string? mbid = null, bool hasFile = false) =>
        new(id, title, disc, number, durationMs, mbid, $"recording-{id}", hasFile);
}
