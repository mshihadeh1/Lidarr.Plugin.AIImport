using System.Reflection;
using Lidarr.Plugin.AIImport.Filesystem;
using Lidarr.Plugin.AIImport.Lidarr;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.History;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.MediaFiles.TrackImport.Manual;
using NzbDrone.Core.Music;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Qualities;

namespace Lidarr.Plugin.AIImport.Tests;

[TestFixture]
public sealed class ImportedSourceReconcilerTests
{
    [Test]
    public void Complete_current_hardlink_evidence_is_already_imported()
    {
        var fixture = Fixture(destinationInode: 20);

        var result = fixture.Reconciler.Classify(fixture.Source, "download-1");

        Assert.That(result, Is.EqualTo(ExistingImportClassification.AlreadyImported));
    }

    [Test]
    public void A_destination_with_a_different_inode_is_indeterminate_not_suppressed_as_imported()
    {
        var fixture = Fixture(destinationInode: 21);

        var result = fixture.Reconciler.Classify(fixture.Source, "download-1");

        Assert.That(result, Is.EqualTo(ExistingImportClassification.Indeterminate));
    }

    [Test]
    public void No_matching_history_remains_pending()
    {
        var fixture = Fixture(destinationInode: 20, history: []);

        var result = fixture.Reconciler.Classify(fixture.Source, "download-1");

        Assert.That(result, Is.EqualTo(ExistingImportClassification.Pending));
    }

    [Test]
    public void Source_replacement_between_identity_reads_is_indeterminate()
    {
        var fixture = Fixture(destinationInode: 20, sourceAfterInode: 22);

        var result = fixture.Reconciler.Classify(fixture.Source, "download-1");

        Assert.That(result, Is.EqualTo(ExistingImportClassification.Indeterminate));
    }

    [Test]
    public void Track_binding_change_between_authoritative_reads_is_indeterminate()
    {
        var fixture = Fixture(destinationInode: 20, mutateTrackOnSecondRead: true);

        var result = fixture.Reconciler.Classify(fixture.Source, "download-1");

        Assert.That(result, Is.EqualTo(ExistingImportClassification.Indeterminate));
    }

    private static TestFixtureData Fixture(
        ulong destinationInode,
        IReadOnlyList<EntityHistory>? history = null,
        ulong? sourceAfterInode = null,
        bool mutateTrackOnSecondRead = false)
    {
        const string sourcePath = "/downloads/Artist/Album/01.flac";
        const string importedPath = "/music/Artist/Album/01.flac";
        var quality = new QualityModel();
        var artist = new Artist { Id = 5 };
        var album = new Album { Id = 10, ArtistId = 5 };
        var release = new AlbumRelease { Id = 101, AlbumId = 10, Album = new LazyLoaded<Album>(album) };
        var source = new ManualImportItem
        {
            Id = 7,
            Path = sourcePath,
            Size = 123,
            DownloadId = "download-1",
            Quality = quality,
            IndexerFlags = 0
        };
        var eventItem = new EntityHistory
        {
            Id = 30,
            ArtistId = 5,
            AlbumId = 10,
            TrackId = 1001,
            DownloadId = "download-1",
            EventType = EntityHistoryEventType.TrackFileImported,
            Quality = quality,
            Data = new Dictionary<string, string>
            {
                ["fileId"] = "40",
                ["droppedPath"] = sourcePath,
                ["importedPath"] = importedPath,
                ["size"] = "123",
                ["indexerFlags"] = "0"
            }
        };
        var trackFile = new TrackFile
        {
            Id = 40,
            Artist = new LazyLoaded<Artist>(artist),
            AlbumId = 10,
            Path = importedPath,
            Size = 123,
            Quality = quality,
            IndexerFlags = (IndexerFlags)0
        };
        var track = new Track
        {
            Id = 1001,
            TrackFileId = 40,
            Artist = new LazyLoaded<Artist>(artist),
            AlbumRelease = new LazyLoaded<AlbumRelease>(release)
        };

        var historyService = Proxy<IHistoryService>((method, _) => method.Name == "Find"
            ? history?.ToList() ?? new List<EntityHistory> { eventItem }
            : null);
        var mediaFiles = Proxy<IMediaFileService>((method, _) => method.Name == "Get" ? new List<TrackFile> { trackFile } : null);
        var trackReads = 0;
        var tracks = Proxy<ITrackService>((method, _) =>
        {
            if (method.Name != "GetTracks") return null;
            trackReads++;
            if (mutateTrackOnSecondRead && trackReads > 1) track.TrackFileId = 41;
            return new List<Track> { track };
        });
        var sourceReads = 0;
        var identities = new StubIdentities(path => path == sourcePath
            ? Identity(++sourceReads == 1 ? 20 : sourceAfterInode ?? 20)
            : path == importedPath ? Identity(destinationInode) : throw new FileNotFoundException());
        return new TestFixtureData(new ImportedSourceReconciler(historyService, mediaFiles, tracks, identities), source);
    }

    private static FileIdentity Identity(ulong inode) => new(1, inode, 123, 1, 0, 1, 0, 0x8000, 1000, 1000, 2);

    private static T Proxy<T>(Func<MethodInfo, object?[]?, object?> handler) where T : class
    {
        var proxy = DispatchProxy.Create<T, InterfaceProxy>();
        ((InterfaceProxy)(object)proxy).Handler = handler;
        return proxy;
    }

    private class InterfaceProxy : DispatchProxy
    {
        public Func<MethodInfo, object?[]?, object?> Handler { get; set; } = null!;
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => Handler(targetMethod!, args);
    }

    private sealed class StubIdentities(Func<string, FileIdentity> inspect) : IFileIdentityProvider
    {
        public Task<FileIdentity> InspectAsync(string path, CancellationToken cancellationToken) => Task.FromResult(inspect(path));
    }

    private sealed record TestFixtureData(ImportedSourceReconciler Reconciler, ManualImportItem Source);
}
