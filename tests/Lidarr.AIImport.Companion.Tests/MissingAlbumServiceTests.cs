using System.Text.Json.Nodes;
using Lidarr.AIImport.Companion.Library;

namespace Lidarr.AIImport.Companion.Tests;

[TestFixture]
public sealed class MissingAlbumServiceTests
{
    private static readonly Guid ReleaseGroup = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid Release = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly Guid Artist = Guid.Parse("11111111-1111-1111-1111-111111111111");

    [Test]
    public async Task Existing_album_is_returned_without_a_write()
    {
        var client = new FakeLibraryClient
        {
            Existing = new ExistingAlbum(10, ReleaseGroup, 5, Artist, new[] { Release })
        };

        var result = await new MissingAlbumService(client).EnsureAsync(Artist, ReleaseGroup, Release, 7, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(result.Created, Is.False);
            Assert.That(result.AlbumId, Is.EqualTo(10));
            Assert.That(client.AddCalls, Is.Zero);
            Assert.That(client.LookupCalls, Is.Zero);
        });
    }

    [Test]
    public async Task Missing_album_uses_authoritative_lookup_and_selects_only_tagged_release()
    {
        var lookup = Lookup();
        var client = new FakeLibraryClient
        {
            Lookup = lookup,
            Root = new RootFolderSettings(7, "/music", true, 2),
            MetadataProfiles = new[] { new MetadataProfileSettings(4, "Standard"), new MetadataProfileSettings(9, "None") },
            Added = new ExistingAlbum(10, ReleaseGroup, 5, Artist, new[] { Release })
        };

        var result = await new MissingAlbumService(client).EnsureAsync(Artist, ReleaseGroup, Release, 7, CancellationToken.None);

        var payload = client.LastPayload!;
        var releases = payload["releases"]!.AsArray();
        Assert.Multiple(() =>
        {
            Assert.That(result.Created, Is.True);
            Assert.That(payload["anyReleaseOk"]!.GetValue<bool>(), Is.False);
            Assert.That(releases.Single(x => x!["foreignReleaseId"]!.GetValue<string>() == Release.ToString())!["monitored"]!.GetValue<bool>(), Is.True);
            Assert.That(releases.Single(x => x!["foreignReleaseId"]!.GetValue<string>() != Release.ToString())!["monitored"]!.GetValue<bool>(), Is.False);
            Assert.That(payload["artist"]!["rootFolderPath"]!.GetValue<string>(), Is.EqualTo("/music"));
            Assert.That(payload["artist"]!["qualityProfileId"]!.GetValue<int>(), Is.EqualTo(2));
            Assert.That(payload["artist"]!["metadataProfileId"]!.GetValue<int>(), Is.EqualTo(9));
            Assert.That(payload["addOptions"]!["searchForNewAlbum"]!.GetValue<bool>(), Is.False);
        });
    }

    [Test]
    public async Task Indeterminate_post_is_reconciled_by_canonical_release_group()
    {
        var client = new FakeLibraryClient
        {
            Lookup = Lookup(),
            Root = new RootFolderSettings(7, "/music", true, 2),
            MetadataProfiles = new[] { new MetadataProfileSettings(9, "None") },
            AddFailure = new HttpRequestException("connection lost"),
            ExistingAfterFailure = new ExistingAlbum(10, ReleaseGroup, 5, Artist, new[] { Release })
        };

        var result = await new MissingAlbumService(client).EnsureAsync(Artist, ReleaseGroup, Release, 7, CancellationToken.None);

        Assert.That(result.AlbumId, Is.EqualTo(10));
        Assert.That(client.AddCalls, Is.EqualTo(1));
    }

    [Test]
    public void Rejects_lookup_owned_by_a_different_musicbrainz_artist()
    {
        var client = new FakeLibraryClient
        {
            Lookup = Lookup() with { ArtistId = Guid.Parse("99999999-9999-9999-9999-999999999999") },
            Root = new RootFolderSettings(7, "/music", true, 2),
            MetadataProfiles = [new MetadataProfileSettings(9, "None")]
        };

        Assert.ThrowsAsync<InvalidDataException>(() =>
            new MissingAlbumService(client).EnsureAsync(Artist, ReleaseGroup, Release, 7, CancellationToken.None));
    }

    private static AlbumLookupResource Lookup()
    {
        var payload = JsonNode.Parse($$"""
        {
          "foreignAlbumId":"{{ReleaseGroup}}",
          "title":"Album",
          "anyReleaseOk":true,
          "monitored":false,
          "releases":[
            {"foreignReleaseId":"{{Release}}","monitored":false,"title":"Exact"},
            {"foreignReleaseId":"55555555-5555-5555-5555-555555555555","monitored":true,"title":"Other"}
          ],
          "artist":{"foreignArtistId":"{{Artist}}","artistName":"Artist","id":0}
        }
        """)!.AsObject();
        return new AlbumLookupResource(ReleaseGroup, Artist, payload);
    }

    private sealed class FakeLibraryClient : ILidarrLibraryClient
    {
        public ExistingAlbum? Existing { get; set; }
        public ExistingAlbum? ExistingAfterFailure { get; set; }
        public AlbumLookupResource? Lookup { get; set; }
        public RootFolderSettings? Root { get; set; }
        public IReadOnlyList<MetadataProfileSettings> MetadataProfiles { get; set; } = Array.Empty<MetadataProfileSettings>();
        public ExistingAlbum? Added { get; set; }
        public Exception? AddFailure { get; set; }
        public int LookupCalls { get; private set; }
        public int AddCalls { get; private set; }
        public JsonObject? LastPayload { get; private set; }

        public Task<ExistingAlbum?> FindAlbumAsync(Guid releaseGroupId, CancellationToken cancellationToken)
        {
            var result = AddCalls > 0 && ExistingAfterFailure is not null ? ExistingAfterFailure : Existing;
            return Task.FromResult(result);
        }

        public Task<AlbumLookupResource> LookupAlbumAsync(Guid releaseGroupId, CancellationToken cancellationToken)
        {
            LookupCalls++;
            return Task.FromResult(Lookup ?? throw new InvalidOperationException("Missing lookup fixture"));
        }

        public Task<RootFolderSettings> GetRootFolderAsync(int rootFolderId, CancellationToken cancellationToken) =>
            Task.FromResult(Root ?? throw new InvalidOperationException("Missing root fixture"));

        public Task<IReadOnlyList<MetadataProfileSettings>> GetMetadataProfilesAsync(CancellationToken cancellationToken) =>
            Task.FromResult(MetadataProfiles);

        public Task<ExistingAlbum> AddAlbumAsync(JsonObject payload, CancellationToken cancellationToken)
        {
            AddCalls++;
            LastPayload = payload;
            if (AddFailure is not null) throw AddFailure;
            return Task.FromResult(Added ?? throw new InvalidOperationException("Missing add fixture"));
        }
    }
}
