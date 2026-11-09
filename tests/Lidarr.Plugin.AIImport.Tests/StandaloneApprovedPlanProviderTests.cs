using System.Text.Json.Nodes;
using Lidarr.Plugin.AIImport.Approval;
using Lidarr.Plugin.AIImport.Configuration;
using Lidarr.Plugin.AIImport.Discovery;
using Lidarr.Plugin.AIImport.Filesystem;
using Lidarr.Plugin.AIImport.Lidarr;
using Lidarr.Plugin.AIImport.Llm;
using Lidarr.Plugin.AIImport.Matching;
using Lidarr.Plugin.AIImport.Workflows;

namespace Lidarr.Plugin.AIImport.Tests;

[TestFixture]
public sealed class StandaloneApprovedPlanProviderTests
{
    [Test]
    public async Task Fresh_discovery_with_different_queue_album_approves_and_real_candidate_change_is_stale()
    {
        using var temp = new TempDirectory();
        var source = new SourceFileCandidate(
            "source-1", "01 Song.flac", "Artist", "Album", "Song", 1, [1], 180000,
            null, [], 5, 10)
        {
            SourcePath = "/downloads/Album/01 Song.flac",
            Quality = JsonNode.Parse("{\"quality\":{\"id\":1}}")!.AsObject(),
            DownloadId = "download",
            Size = 123,
            ManualImportId = 77
        };
        var selected = Release(10, 101, "selected");
        var queueScoped = Release(20, 201, "queue-scoped");
        var releases = new Dictionary<int, IReadOnlyList<ReleaseCandidate>>
        {
            [10] = [selected],
            [20] = [queueScoped],
            [30] = [selected, queueScoped]
        };
        var gateway = new MutableGateway(
            [new QueueCandidate(1, 20, "failed", TrackedImportState.ImportFailed, "download")],
            [source], releases);
        var store = new StandaloneAnalysisStore(Path.Combine(temp.Path, "analyses.json"));
        var proposal = new MatchProposal(
            1, ProposalStatus.Proposed, 5, 10, 101, .98m, "reviewed",
            [new TrackProposal("source-1", 1001, .99m)]);
        var analysis = new FailedImportDiscoveryWorkflow(
                gateway, new DeterministicMatcher(), new StubLlm(proposal), store)
            .Discover(new AiImportSettings()).Analyses.Single();
        var provider = new StandaloneApprovedPlanProvider(
            store,
            gateway,
            new StubIdentityProvider(new FileIdentity(1, 2, 123, 4, 5, 6, 7, 420, 8, 9, 1)),
            new StubExecutionClient(),
            () => "safety-fingerprint");

        var approved = await provider.BuildCurrentAsync(analysis.AnalysisId, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(approved.AnalysisId, Is.EqualTo(analysis.AnalysisId));
            Assert.That(approved.HumanOverride, Is.False);
            Assert.That(approved.ParentAnalysisId, Is.Null);
        });
        gateway.Queue = [new QueueCandidate(1, 30, "failed", TrackedImportState.ImportFailed, "download")];
        Assert.ThrowsAsync<StaleApprovalException>(() =>
            provider.BuildCurrentAsync(analysis.AnalysisId, CancellationToken.None));

        gateway.Queue = [new QueueCandidate(1, 20, "failed", TrackedImportState.ImportFailed, "download")];
        releases[20] = [queueScoped, Release(20, 202, "new-current-candidate")];
        Assert.ThrowsAsync<StaleApprovalException>(() =>
            provider.BuildCurrentAsync(analysis.AnalysisId, CancellationToken.None));
    }

    [Test]
    public async Task Untracked_folder_analysis_approves_with_empty_download_identity_and_changes_become_stale()
    {
        using var temp = new TempDirectory();
        var albumDir = Path.Combine(temp.Path, "Artist", "Album");
        Directory.CreateDirectory(albumDir);
        var filePath = Path.Combine(albumDir, "01 Song.flac");
        File.WriteAllText(filePath, "");
        var source = new SourceFileCandidate(
            "source-1", "01 Song.flac", "Artist", "Album", "Song", 1, [1], 180000,
            null, [], 5, 10)
        {
            SourcePath = filePath,
            Quality = JsonNode.Parse("{\"quality\":{\"id\":1}}")!.AsObject(),
            DownloadId = null,
            Size = 123,
            ManualImportId = 77
        };
        var releases = new Dictionary<int, IReadOnlyList<ReleaseCandidate>> { [10] = [Release(10, 101, "release-101")] };
        var gateway = new MutableGateway(
            [], [source], releases,
            new Dictionary<string, ManualImportScanResult> { [albumDir] = new([source], 0, 0) });
        var store = new StandaloneAnalysisStore(Path.Combine(temp.Path, "analyses.json"));
        var settings = new AiImportSettings();
        settings.ScanRoots = temp.Path;
        var analysis = new FailedImportDiscoveryWorkflow(
                gateway, new DeterministicMatcher(), new StubLlm(new MatchProposal(
                    1, ProposalStatus.Proposed, 5, 10, 101, .98m, "reviewed",
                    [new TrackProposal("source-1", 1001, .99m)])), store)
            .Discover(settings).Analyses.Single();
        var provider = new StandaloneApprovedPlanProvider(
            store,
            gateway,
            new StubIdentityProvider(new FileIdentity(1, 2, 123, 4, 5, 6, 7, 420, 8, 9, 1)),
            new StubExecutionClient(),
            () => "safety-fingerprint");

        var approved = await provider.BuildCurrentAsync(analysis.AnalysisId, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(approved.FolderPath, Is.EqualTo(albumDir));
            Assert.That(approved.DownloadId, Is.EqualTo(string.Empty));
            Assert.That(approved.Files.Single().DownloadId, Is.EqualTo(string.Empty));
        });

        gateway.UntrackedScans[albumDir] = new([source with { Size = 124 }], 0, 0);
        Assert.ThrowsAsync<StaleApprovalException>(() =>
            provider.BuildCurrentAsync(analysis.AnalysisId, CancellationToken.None));
    }

    private static ReleaseCandidate Release(int albumId, int releaseId, string foreignId) =>
        new(albumId, releaseId, "Artist", "Album", foreignId, "Album", string.Empty,
            [new TrackCandidate(releaseId == 101 ? 1001 : releaseId * 10, "Song", 1, "1", 180000, null, null, false)], 5);

    private sealed class MutableGateway(
        IReadOnlyList<QueueCandidate> queue,
        IReadOnlyList<SourceFileCandidate> files,
        IReadOnlyDictionary<int, IReadOnlyList<ReleaseCandidate>> releases,
        IReadOnlyDictionary<string, ManualImportScanResult>? untrackedScans = null) : IInProcessLidarrGateway
    {
        public IReadOnlyList<QueueCandidate> Queue { get; set; } = queue;
        public Dictionary<string, ManualImportScanResult> UntrackedScans { get; } =
            new(untrackedScans ?? new Dictionary<string, ManualImportScanResult>(), StringComparer.Ordinal);
        public string Version => FailedImportDiscoveryWorkflow.CompatibleLidarrVersion;
        public IReadOnlyList<QueueCandidate> GetTrackedDownloads() => Queue;
        public IReadOnlyList<SourceFileCandidate> GetManualImports(string downloadId) => files;
        public ManualImportScanResult GetUntrackedManualImportScan(string folderPath) =>
            UntrackedScans.TryGetValue(folderPath, out var scan) ? scan : new ManualImportScanResult([], 0, 0);
        public IReadOnlyList<ReleaseCandidate> GetReleaseCandidates(int albumId) =>
            releases.TryGetValue(albumId, out var value) ? value : [];
    }

    private sealed class StubLlm(MatchProposal proposal) : ILlmMatchService
    {
        public MatchProposal Match(
            AiImportSettings settings,
            IReadOnlyList<SourceFileCandidate> files,
            IReadOnlyList<ReleaseCandidate> candidates,
            IReadOnlyList<int> rankedReleaseIds) => proposal;
    }

    private sealed class StubIdentityProvider(FileIdentity identity) : IFileIdentityProvider
    {
        public Task<FileIdentity> InspectAsync(string path, CancellationToken cancellationToken) => Task.FromResult(identity);
    }

    private sealed class StubExecutionClient : ILidarrExecutionClient
    {
        public Task<string> GetArtistPathAsync(int artistId, CancellationToken cancellationToken) =>
            Task.FromResult("/music/Artist");
        public Task RevalidateManualImportAsync(ApprovedImportPlan plan, CancellationToken cancellationToken) =>
            Task.CompletedTask;
        public Task<LidarrCommandReceipt> SubmitManualImportAsync(ApprovedImportPlan plan, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class TempDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ai-import-plan-" + Guid.NewGuid().ToString("N"));
        public TempDirectory() => Directory.CreateDirectory(Path);
        public void Dispose() => Directory.Delete(Path, true);
    }
}
