using Lidarr.Plugin.AIImport.Approval;
using Lidarr.Plugin.AIImport.Discovery;
using Lidarr.Plugin.AIImport.Lidarr;
using Lidarr.Plugin.AIImport.Matching;
using Lidarr.Plugin.AIImport.Workflows;

namespace Lidarr.Plugin.AIImport.Tests;

[TestFixture]
public sealed class LibraryAdditionServiceTests
{
    private const string ArtistMbId = "11111111-1111-1111-1111-111111111111";
    private const string AlbumMbId = "22222222-2222-2222-2222-222222222222";
    private const string ReleaseMbId = "33333333-3333-3333-3333-333333333333";

    [Test]
    public void Exact_consistent_musicbrainz_tags_allow_a_separate_reviewed_library_addition()
    {
        using var temp = new TempDirectory();
        var source = Source();
        var gateway = new StubGateway([new QueueCandidate(1, null, "failed", TrackedImportState.ImportFailed, "download")], [source]);
        var store = new StandaloneAnalysisStore(Path.Combine(temp.Path, "analyses.json"));
        var proposal = new MatchProposal(1, ProposalStatus.ManualReview, null, null, null, 0m, "No Lidarr candidate.", []);
        var analysisId = FailedImportDiscoveryWorkflow.ComputeAnalysisId("download", AiImportSourceKinds.Queue, null, [source], [], proposal);
        store.Replace([new StoredStandaloneAnalysis(analysisId, "download", proposal)]);
        var client = new StubClient();
        Assert.Throws<ArgumentException>(() =>
            new LibraryAdditionService(store, gateway, client).Add(analysisId, 2, 3, 4, "add"));

        var created = new LibraryAdditionService(store, gateway, client)
            .Add(analysisId, 2, 3, 4, "ADD");

        Assert.Multiple(() =>
        {
            Assert.That(client.Request, Is.EqualTo(new LibraryAdditionRequest(ArtistMbId, AlbumMbId, ReleaseMbId, 2, 3, 4)));
            Assert.That(created.ArtistId, Is.EqualTo(5));
            Assert.That(created.AlbumId, Is.EqualTo(10));
        });
    }

    [Test]
    public void Conflicting_musicbrainz_artist_tags_fail_without_a_library_write()
    {
        using var temp = new TempDirectory();
        var sources = new[]
        {
            Source(),
            Source() with { Id = "source-2", ArtistMusicBrainzId = "33333333-3333-3333-3333-333333333333" }
        };
        var gateway = new StubGateway([new QueueCandidate(1, null, "failed", TrackedImportState.ImportFailed, "download")], sources);
        var store = new StandaloneAnalysisStore(Path.Combine(temp.Path, "analyses.json"));
        var proposal = new MatchProposal(1, ProposalStatus.ManualReview, null, null, null, 0m, "No Lidarr candidate.", []);
        var analysisId = FailedImportDiscoveryWorkflow.ComputeAnalysisId("download", AiImportSourceKinds.Queue, null, sources, [], proposal);
        store.Replace([new StoredStandaloneAnalysis(analysisId, "download", proposal)]);
        var client = new StubClient();

        Assert.Throws<InvalidOperationException>(() =>
            new LibraryAdditionService(store, gateway, client).Add(analysisId, 2, 3, 4, "ADD"));
        Assert.That(client.Request, Is.Null);
    }

    [Test]
    public void Missing_or_ambiguous_release_identity_never_offers_library_creation()
    {
        Assert.Multiple(() =>
        {
            Assert.That(FailedImportDiscoveryWorkflow.GetLibraryAddition(
                [Source() with { ReleaseMusicBrainzId = null }]), Is.Null);
            Assert.That(FailedImportDiscoveryWorkflow.GetLibraryAddition(
                [Source(), Source() with { Id = "source-2", ReleaseMusicBrainzId = "44444444-4444-4444-4444-444444444444" }]), Is.Null);
        });
    }

    [Test]
    public void Untracked_folder_analysis_allows_library_addition_without_a_queue_row()
    {
        using var temp = new TempDirectory();
        var folder = Path.Combine(temp.Path, "New Artist", "New Album");
        var source = Source();
        var gateway = new StubGateway([], []);
        gateway.UntrackedScans[folder] = new([source], 0, 0);
        var store = new StandaloneAnalysisStore(Path.Combine(temp.Path, "analyses.json"));
        var proposal = new MatchProposal(1, ProposalStatus.ManualReview, null, null, null, 0m, "No Lidarr candidate.", []);
        var analysisId = FailedImportDiscoveryWorkflow.ComputeAnalysisId(
            folder, AiImportSourceKinds.Folder, null, [source], [], proposal);
        store.Replace([new StoredStandaloneAnalysis(analysisId, string.Empty, proposal) { FolderPath = folder }]);
        var client = new StubClient();

        var created = new LibraryAdditionService(store, gateway, client).Add(analysisId, 2, 3, 4, "ADD");

        Assert.Multiple(() =>
        {
            Assert.That(client.Request, Is.EqualTo(new LibraryAdditionRequest(ArtistMbId, AlbumMbId, ReleaseMbId, 2, 3, 4)));
            Assert.That(created.ArtistId, Is.EqualTo(5));
            Assert.That(created.AlbumId, Is.EqualTo(10));
        });
    }

    [Test]
    public void Untracked_folder_addition_requires_the_current_folder_scan()
    {
        using var temp = new TempDirectory();
        var folder = Path.Combine(temp.Path, "New Artist", "New Album");
        var source = Source();
        var gateway = new StubGateway([], []);
        gateway.UntrackedScans[folder] = new([source], 0, 0);
        var store = new StandaloneAnalysisStore(Path.Combine(temp.Path, "analyses.json"));
        var proposal = new MatchProposal(1, ProposalStatus.ManualReview, null, null, null, 0m, "No Lidarr candidate.", []);
        var analysisId = FailedImportDiscoveryWorkflow.ComputeAnalysisId(
            folder, AiImportSourceKinds.Folder, null, [source], [], proposal);
        store.Replace([new StoredStandaloneAnalysis(analysisId, string.Empty, proposal) { FolderPath = folder }]);
        gateway.UntrackedScans[folder] = new([source, Source() with { Id = "source-2" }], 0, 0);

        Assert.Throws<StaleApprovalException>(() =>
            new LibraryAdditionService(store, gateway, new StubClient()).Add(analysisId, 2, 3, 4, "ADD"));
    }

    private static SourceFileCandidate Source() =>
        new("source-1", "01 Song.flac", "New Artist", "New Album", "Song", 1, [1], 180000,
            null, [], null, null, ArtistMbId, AlbumMbId, ReleaseMbId);

    private sealed class StubGateway(
        IReadOnlyList<QueueCandidate> queue,
        IReadOnlyList<SourceFileCandidate> files) : IInProcessLidarrGateway
    {
        public Dictionary<string, ManualImportScanResult> UntrackedScans { get; } =
            new(StringComparer.Ordinal);
        public string Version => FailedImportDiscoveryWorkflow.CompatibleLidarrVersion;
        public IReadOnlyList<QueueCandidate> GetTrackedDownloads() => queue;
        public IReadOnlyList<SourceFileCandidate> GetManualImports(string downloadId) => files;
        public ManualImportScanResult GetUntrackedManualImportScan(string folderPath) =>
            UntrackedScans.TryGetValue(folderPath, out var scan) ? scan : new ManualImportScanResult([], 0, 0);
        public IReadOnlyList<ReleaseCandidate> GetReleaseCandidates(int albumId) => [];
    }

    private sealed class StubClient : ILibraryAdditionClient
    {
        public LibraryAdditionRequest? Request { get; private set; }
        public LibraryAdditionCreatedDto EnsureAlbum(LibraryAdditionRequest request)
        {
            Request = request;
            return new LibraryAdditionCreatedDto(
                5, 10, 20, request.ArtistMusicBrainzId, request.AlbumMusicBrainzId, request.ReleaseMusicBrainzId);
        }
    }

    private sealed class TempDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ai-import-library-" + Guid.NewGuid().ToString("N"));
        public TempDirectory() => Directory.CreateDirectory(Path);
        public void Dispose() => Directory.Delete(Path, true);
    }
}
