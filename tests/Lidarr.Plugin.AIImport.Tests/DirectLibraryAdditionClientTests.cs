using System.Reflection;
using Lidarr.Plugin.AIImport.Approval;
using Lidarr.Plugin.AIImport.Lidarr;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.MetadataSource;
using NzbDrone.Core.Music;
using NzbDrone.Core.Profiles.Metadata;
using NzbDrone.Core.Profiles.Qualities;
using NzbDrone.Core.RootFolders;

namespace Lidarr.Plugin.AIImport.Tests;

[TestFixture]
public sealed class DirectLibraryAdditionClientTests
{
    private const string ArtistMbId = "11111111-1111-1111-1111-111111111111";
    private const string AlbumMbId = "22222222-2222-2222-2222-222222222222";
    private const string ReleaseMbId = "33333333-3333-3333-3333-333333333333";

    [Test]
    public void Rejects_noncanonical_or_zero_musicbrainz_ids_before_calling_lidarr()
    {
        var client = Client(_ => throw new AssertionException("No Lidarr service may be called for invalid identities."));
        Assert.Multiple(() =>
        {
            Assert.Throws<InvalidDataException>(() => client.EnsureAlbum(Request("{11111111-1111-1111-1111-111111111111}")));
            Assert.Throws<InvalidDataException>(() => client.EnsureAlbum(Request("00000000-0000-0000-0000-000000000000")));
        });
    }

    [Test]
    public void Existing_artist_must_match_the_explicit_root_and_profiles_before_any_mutation()
    {
        var addCalls = 0;
        var remoteAlbum = new Album
        {
            ForeignAlbumId = AlbumMbId,
            AlbumReleases = new LazyLoaded<List<AlbumRelease>>(
                [new AlbumRelease { ForeignReleaseId = ReleaseMbId }])
        };
        var client = Client(call => call.Service switch
        {
            nameof(IRootFolderService) when call.Method == "Get" => new RootFolder { Id = 1, Path = "/music", Accessible = true },
            nameof(IQualityProfileService) when call.Method == "Exists" => true,
            nameof(IMetadataProfileService) when call.Method == "Exists" => true,
            nameof(IProvideAlbumInfo) when call.Method == "GetAlbumInfo" =>
                Tuple.Create(ArtistMbId, remoteAlbum, new List<ArtistMetadata>()),
            nameof(IArtistService) when call.Method == "FindById" => new Artist
            {
                Id = 7, ForeignArtistId = ArtistMbId, RootFolderPath = "/other",
                QualityProfileId = 2, MetadataProfileId = 3
            },
            nameof(IAddAlbumService) => CountAndReturn(ref addCalls),
            _ => throw new AssertionException($"Unexpected call {call.Service}.{call.Method}")
        });

        Assert.Throws<InvalidOperationException>(() => client.EnsureAlbum(Request()));
        Assert.That(addCalls, Is.Zero, "A settings mismatch must fail before any Lidarr mutation.");
    }

    private static object? CountAndReturn(ref int count)
    {
        count++;
        return null;
    }

    private static LibraryAdditionRequest Request(string artist = ArtistMbId) =>
        new(artist, AlbumMbId, ReleaseMbId, 1, 2, 3);

    private static DirectLibraryAdditionClient Client(Func<Invocation, object?> handler) => new(
        Proxy<IArtistService>(handler), Proxy<IAlbumService>(handler), Proxy<IAddAlbumService>(handler),
        Proxy<IProvideAlbumInfo>(handler), Proxy<IRefreshAlbumService>(handler), Proxy<IReleaseService>(handler),
        Proxy<ITrackService>(handler), Proxy<IRootFolderService>(handler), Proxy<IQualityProfileService>(handler),
        Proxy<IMetadataProfileService>(handler));

    private static T Proxy<T>(Func<Invocation, object?> handler) where T : class
    {
        var proxy = DispatchProxy.Create<T, InterfaceProxy>();
        ((InterfaceProxy)(object)proxy).Service = typeof(T).Name;
        ((InterfaceProxy)(object)proxy).Handler = handler;
        return proxy;
    }

    private sealed record Invocation(string Service, string Method, object?[] Arguments);

    private class InterfaceProxy : DispatchProxy
    {
        public string Service { get; set; } = string.Empty;
        public Func<Invocation, object?> Handler { get; set; } = null!;
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
            Handler(new Invocation(Service, targetMethod?.Name ?? string.Empty, args ?? []));
    }
}
