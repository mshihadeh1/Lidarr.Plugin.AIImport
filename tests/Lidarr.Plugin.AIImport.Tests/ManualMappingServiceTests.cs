using Lidarr.Plugin.AIImport.Approval;
using Lidarr.Plugin.AIImport.Discovery;
using Lidarr.Plugin.AIImport.Lidarr;
using Lidarr.Plugin.AIImport.Matching;
using Lidarr.Plugin.AIImport.Workflows;

namespace Lidarr.Plugin.AIImport.Tests;

[TestFixture]
public sealed class ManualMappingServiceTests
{
    [Test]
    public void Human_review_creates_a_zero_confidence_proposal_from_current_verified_ids()
    {
        using var temp = new TempDirectory();
        var source = Source();
        var releases = new Dictionary<int, IReadOnlyList<ReleaseCandidate>>
        {
            [10] = [Release(101), Release(102)]
        };
        var gateway = new MutableGateway(
            [new QueueCandidate(1, 10, "failed", TrackedImportState.ImportFailed, "download")],
            [source], releases);
        var store = new StandaloneAnalysisStore(Path.Combine(temp.Path, "analyses.json"));
        var manual = new MatchProposal(1, ProposalStatus.ManualReview, null, null, null, 0m, "Human review required.", []);
        var originalId = FailedImportDiscoveryWorkflow.ComputeAnalysisId("download", 10, [source], releases[10], manual);
        store.Replace([new StoredStandaloneAnalysis(originalId, "download", manual)]);
        var service = new ManualMappingService(store, gateway);

        var created = service.Create(originalId, 101, "[{\"sourceFileId\":\"source-1\",\"trackId\":1001}]", "REVIEW");
        var stored = store.GetRequired(created.AnalysisId);

        Assert.Multiple(() =>
        {
            Assert.That(created.AnalysisId, Is.Not.EqualTo(originalId));
            Assert.That(stored.Proposal.Status, Is.EqualTo(ProposalStatus.Proposed));
            Assert.That(stored.Proposal.Confidence, Is.Zero);
            Assert.That(stored.Proposal.ReleaseId, Is.EqualTo(101));
            Assert.That(stored.Proposal.Tracks.Single(), Is.EqualTo(new TrackProposal("source-1", 1001, 0m)));
            Assert.That(stored.HumanOverride, Is.True);
            Assert.That(stored.ParentAnalysisId, Is.EqualTo(originalId));
            Assert.That(stored.ExpiresAt, Is.GreaterThan(DateTimeOffset.UtcNow));
        });
        store.Replace([new StoredStandaloneAnalysis(originalId, "download", manual)]);
        Assert.That(store.GetRequired(created.AnalysisId).HumanOverride, Is.True,
            "A fresh discovery must retain an unexpired human-reviewed proposal needed by approval revalidation.");
    }

    [Test]
    public void Human_review_rejects_track_ids_outside_the_selected_verified_release()
    {
        using var temp = new TempDirectory();
        var source = Source();
        var releases = new Dictionary<int, IReadOnlyList<ReleaseCandidate>> { [10] = [Release(101)] };
        var gateway = new MutableGateway(
            [new QueueCandidate(1, 10, "failed", TrackedImportState.ImportFailed, "download")],
            [source], releases);
        var store = new StandaloneAnalysisStore(Path.Combine(temp.Path, "analyses.json"));
        var manual = new MatchProposal(1, ProposalStatus.ManualReview, null, null, null, 0m, "Human review required.", []);
        var originalId = FailedImportDiscoveryWorkflow.ComputeAnalysisId("download", 10, [source], releases[10], manual);
        store.Replace([new StoredStandaloneAnalysis(originalId, "download", manual)]);

        Assert.Throws<ProposalValidationException>(() => new ManualMappingService(store, gateway)
            .Create(originalId, 101, "[{\"sourceFileId\":\"source-1\",\"trackId\":9999}]", "REVIEW"));
    }

    [Test]
    public void Human_review_requires_manual_status_exact_confirmation_and_strict_mapping_json()
    {
        using var temp = new TempDirectory();
        var source = Source();
        var releases = new Dictionary<int, IReadOnlyList<ReleaseCandidate>> { [10] = [Release(101)] };
        var gateway = new MutableGateway(
            [new QueueCandidate(1, 10, "failed", TrackedImportState.ImportFailed, "download")],
            [source], releases);
        var store = new StandaloneAnalysisStore(Path.Combine(temp.Path, "analyses.json"));
        var manual = new MatchProposal(1, ProposalStatus.ManualReview, null, null, null, 0m, "review", []);
        var originalId = FailedImportDiscoveryWorkflow.ComputeAnalysisId("download", 10, [source], releases[10], manual);
        store.Replace([new StoredStandaloneAnalysis(originalId, "download", manual)]);
        var service = new ManualMappingService(store, gateway);
        const string valid = "[{\"sourceFileId\":\"source-1\",\"trackId\":1001}]";

        Assert.Multiple(() =>
        {
            Assert.Throws<ArgumentException>(() => service.Create(originalId, 101, valid, "review"));
            Assert.Throws<ProposalValidationException>(() => service.Create(
                originalId, 101, "[{\"sourceFileId\":\"source-1\",\"trackId\":1001,\"artistId\":5}]", "REVIEW"));
            Assert.Throws<ProposalValidationException>(() => service.Create(
                originalId, 101, "[{\"sourceFileId\":\"source-1\",\"sourceFileId\":\"source-1\",\"trackId\":1001}]", "REVIEW"));
            Assert.Throws<ProposalValidationException>(() => service.Create(originalId, 101, "[]", "REVIEW"));
        });

        var proposed = new MatchProposal(
            1, ProposalStatus.Proposed, 5, 10, 101, 0m, "already proposed", [new TrackProposal("source-1", 1001, 0m)]);
        var proposedId = FailedImportDiscoveryWorkflow.ComputeAnalysisId("download", 10, [source], releases[10], proposed);
        store.Replace([new StoredStandaloneAnalysis(proposedId, "download", proposed)]);
        Assert.Throws<InvalidOperationException>(() => service.Create(proposedId, 101, valid, "REVIEW"));
    }

    [Test]
    public void Human_review_expiry_is_enforced_on_every_read_at_the_exact_boundary()
    {
        using var temp = new TempDirectory();
        var now = DateTimeOffset.Parse("2026-11-01T12:00:00Z");
        var clock = new FixedTimeProvider(now);
        var store = new StandaloneAnalysisStore(Path.Combine(temp.Path, "analyses.json"), clock);
        var proposal = new MatchProposal(
            1, ProposalStatus.Proposed, 5, 10, 101, 0m, "human",
            [new TrackProposal("source-1", 1001, 0m)]);
        var parent = new MatchProposal(1, ProposalStatus.ManualReview, null, null, null, 0m, "review", []);
        store.Replace([
            new StoredStandaloneAnalysis("parent", "download", parent),
            new StoredStandaloneAnalysis("override", "download", proposal, true, "parent", now.AddHours(1))
        ]);

        Assert.That(store.GetRequired("override").AnalysisId, Is.EqualTo("override"));
        clock.UtcNow = now.AddHours(1);
        Assert.Throws<StaleApprovalException>(() => store.GetRequired("override"));

        var restarted = new StandaloneAnalysisStore(Path.Combine(temp.Path, "analyses.json"), clock);
        Assert.Throws<StaleApprovalException>(() => restarted.GetRequired("override"));
    }

    [Test]
    public void Human_review_requires_a_current_manual_review_parent()
    {
        using var temp = new TempDirectory();
        var proposal = new MatchProposal(
            1, ProposalStatus.Proposed, 5, 10, 101, 0m, "human",
            [new TrackProposal("source-1", 1001, 0m)]);
        var store = new StandaloneAnalysisStore(Path.Combine(temp.Path, "analyses.json"));
        store.Replace([new StoredStandaloneAnalysis(
            "override", "download", proposal, true, "missing-parent", DateTimeOffset.UtcNow.AddHours(1))]);

        Assert.Throws<StaleApprovalException>(() => store.GetRequired("override"));
    }

    private static SourceFileCandidate Source() =>
        new("source-1", "01 Song.flac", "Artist", "Album", "Song", 1, [1], 180000, null, [], 5, 10);

    private static ReleaseCandidate Release(int id) =>
        new(10, id, "Artist", "Album", "release-" + id, "Album", string.Empty,
            [new TrackCandidate(id == 101 ? 1001 : 1002, "Song", 1, "1", 180000, null, null, false)], 5);

    private sealed class MutableGateway(
        IReadOnlyList<QueueCandidate> queue,
        IReadOnlyList<SourceFileCandidate> files,
        IReadOnlyDictionary<int, IReadOnlyList<ReleaseCandidate>> releases) : IInProcessLidarrGateway
    {
        public string Version => FailedImportDiscoveryWorkflow.CompatibleLidarrVersion;
        public IReadOnlyList<QueueCandidate> GetTrackedDownloads() => queue;
        public IReadOnlyList<SourceFileCandidate> GetManualImports(string downloadId) => files;
        public IReadOnlyList<ReleaseCandidate> GetReleaseCandidates(int albumId) =>
            releases.TryGetValue(albumId, out var value) ? value : [];
    }

    private sealed class TempDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ai-import-manual-" + Guid.NewGuid().ToString("N"));
        public TempDirectory() => Directory.CreateDirectory(Path);
        public void Dispose() => Directory.Delete(Path, true);
    }

    private sealed class FixedTimeProvider(DateTimeOffset value) : TimeProvider
    {
        public DateTimeOffset UtcNow { get; set; } = value;
        public override DateTimeOffset GetUtcNow() => UtcNow;
    }
}
