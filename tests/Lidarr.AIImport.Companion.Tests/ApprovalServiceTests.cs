using System.Text.Json;
using System.Text.Json.Nodes;
using Lidarr.AIImport.Companion.Approval;
using Lidarr.AIImport.Companion.Discovery;
using Lidarr.AIImport.Companion.Filesystem;
using Lidarr.AIImport.Companion.Lidarr;
using Lidarr.AIImport.Companion.Models;
using Lidarr.AIImport.Companion.Safety;

namespace Lidarr.AIImport.Companion.Tests;

[TestFixture]
public sealed class ApprovalServiceTests
{
    [Test]
    public async Task Revalidates_analysis_and_persists_exact_source_and_track_snapshot()
    {
        using var temp = new TempDirectory();
        var analysis = Analysis("analysis-1");
        var store = new ApprovalStore(Path.Combine(temp.Path, "approvals.json"));
        var service = new ApprovalService(
            new FakeDiscovery(analysis),
            new FakeIdentityProvider(new FileIdentity(1, 2, 123, 4, 5, 8, 9, 420, 6, 7, 1)),
            store,
            new FakeSafetyProvider(),
            new FakeExecutionClient());

        var created = await service.ApproveAsync("analysis-1", TimeSpan.FromMinutes(10), CancellationToken.None);
        var record = await store.GetAsync(created.ApprovalId, CancellationToken.None);
        var plan = record!.Payload.Deserialize<ApprovedImportPlan>();

        Assert.Multiple(() =>
        {
            Assert.That(plan!.AnalysisId, Is.EqualTo("analysis-1"));
            Assert.That(plan.DestinationRoot, Is.EqualTo("/music/Artist"));
            Assert.That(plan.ReleaseId, Is.EqualTo(101));
            Assert.That(plan.SafetyFingerprint, Is.EqualTo("safety-fingerprint"));
            Assert.That(plan.Files.Single().SourcePath, Is.EqualTo("/downloads/track.flac"));
            Assert.That(plan.Files.Single().TrackId, Is.EqualTo(1001));
            Assert.That(plan.Files.Single().Identity.Inode, Is.EqualTo(2));
            Assert.That(plan.Files.Single().Quality["quality"]!["id"]!.GetValue<int>(), Is.EqualTo(1));
        });
    }

    [Test]
    public void Rejects_stale_or_nonproposed_analysis_without_creating_approval()
    {
        using var temp = new TempDirectory();
        var store = new ApprovalStore(Path.Combine(temp.Path, "approvals.json"));
        var service = new ApprovalService(
            new FakeDiscovery(Analysis("different")),
            new FakeIdentityProvider(new FileIdentity(1, 2, 3, 4, 5, 8, 9, 420, 6, 7, 1)),
            store,
            new FakeSafetyProvider(),
            new FakeExecutionClient());

        Assert.ThrowsAsync<StaleApprovalException>(() =>
            service.ApproveAsync("analysis-1", TimeSpan.FromMinutes(10), CancellationToken.None));
    }

    private static AnalysisResult Analysis(string id)
    {
        var source = new SourceFileCandidate(
            Id: "source-1",
            Name: "track.flac",
            Artist: "Artist",
            Album: "Album",
            Title: "Track",
            DiscNumber: 1,
            TrackNumbers: [1],
            DurationMs: 180000,
            TrackMusicBrainzId: null,
            Rejections: [],
            ArtistId: 5,
            AlbumId: 10,
            SourcePath: "/downloads/track.flac",
            Quality: JsonNode.Parse("{\"quality\":{\"id\":1}}")!.AsObject(),
            IndexerFlags: 0,
            DownloadId: "download-1",
            Size: 123,
            ManualImportId: 77);
        var proposal = new MatchProposal(
            1, ProposalStatus.Proposed, 5, 10, 101, 0.99m, "verified",
            [new TrackProposal("source-1", 1001, 0.99m)]);
        return new AnalysisResult(id, "download-1", "Album", proposal, [source], [], false, DateTimeOffset.UtcNow);
    }

    private sealed class FakeDiscovery(params AnalysisResult[] analyses) : IFailedImportDiscoveryService
    {
        public Task<IReadOnlyList<AnalysisResult>> DiscoverAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<AnalysisResult>>(analyses);
    }

    private sealed class FakeIdentityProvider(FileIdentity identity) : IFileIdentityProvider
    {
        public Task<FileIdentity> InspectAsync(string path, CancellationToken cancellationToken) => Task.FromResult(identity);
    }

    private sealed class FakeSafetyProvider : IImportSafetySnapshotProvider
    {
        public Task<ImportSafetySnapshot> CaptureAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new ImportSafetySnapshot("safety-fingerprint", "helper-hash"));
    }

    private sealed class FakeExecutionClient : ILidarrExecutionClient
    {
        public Task<string> GetArtistPathAsync(int artistId, CancellationToken cancellationToken) =>
            Task.FromResult("/music/Artist");
        public Task RevalidateManualImportAsync(ApprovedImportPlan plan, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<LidarrCommandReceipt> SubmitManualImportAsync(ApprovedImportPlan plan, CancellationToken cancellationToken) =>
            throw new AssertionException("Approval must not submit an import command.");
    }

    private sealed class TempDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ai-import-approval-service-" + Guid.NewGuid().ToString("N"));
        public TempDirectory() => Directory.CreateDirectory(Path);
        public void Dispose() => Directory.Delete(Path, true);
    }
}
