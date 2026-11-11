using System.Reflection;
using Lidarr.Plugin.AIImport.Approval;
using Lidarr.Plugin.AIImport.Configuration;
using Lidarr.Plugin.AIImport.Discovery;
using Lidarr.Plugin.AIImport.Lidarr;
using NzbDrone.Core.Music;
using NzbDrone.Core.Profiles.Metadata;
using NzbDrone.Core.Profiles.Qualities;
using NzbDrone.Core.RootFolders;

namespace Lidarr.Plugin.AIImport.Tests;

[TestFixture]
public sealed class YoloLibraryAdderTests
{
    private const string ArtistMbId = "11111111-1111-1111-1111-111111111111";
    private const string AlbumMbId = "22222222-2222-2222-2222-222222222222";
    private const string ReleaseMbId = "33333333-3333-3333-3333-333333333333";

    [Test]
    public void Configured_defaults_do_not_override_an_existing_artists_destination()
    {
        var artist = new Artist
        {
            Id = 7,
            ForeignArtistId = ArtistMbId,
            RootFolderPath = "/media/Music",
            QualityProfileId = 1,
            MetadataProfileId = 1
        };
        var root = new RootFolder { Id = 6, Path = "/media/Music", Accessible = true };
        var client = new CapturingLibraryAdditionClient();
        var adder = new YoloLibraryAdder(
            Proxy<IArtistService>(call => call.Method == "FindById" ? artist : Unexpected(call)),
            Proxy<IRootFolderService>(call => call.Method == "All" ? new List<RootFolder> { root } : Unexpected(call)),
            Proxy<IQualityProfileService>(Unexpected),
            Proxy<IMetadataProfileService>(Unexpected),
            client);
        var settings = new AiImportSettings
        {
            YoloRootFolderId = 99,
            YoloQualityProfileId = 2,
            YoloMetadataProfileId = 3
        };

        adder.TryAddTaggedAlbum(new LibraryAdditionDto(ArtistMbId, AlbumMbId, ReleaseMbId, "Artist", "Album"), settings);

        Assert.Multiple(() =>
        {
            Assert.That(client.Request, Is.Not.Null);
            Assert.That(client.Request!.RootFolderId, Is.EqualTo(6));
            Assert.That(client.Request.QualityProfileId, Is.EqualTo(1));
            Assert.That(client.Request.MetadataProfileId, Is.EqualTo(1));
        });
    }

    private static object? Unexpected(Invocation call) =>
        throw new AssertionException($"Unexpected call {call.Service}.{call.Method}");

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

    private sealed class CapturingLibraryAdditionClient : ILibraryAdditionClient
    {
        public LibraryAdditionRequest? Request { get; private set; }

        public LibraryAdditionCreatedDto EnsureAlbum(LibraryAdditionRequest request)
        {
            Request = request;
            return new LibraryAdditionCreatedDto(7, 10, 101, ArtistMbId, AlbumMbId, ReleaseMbId);
        }
    }
}
