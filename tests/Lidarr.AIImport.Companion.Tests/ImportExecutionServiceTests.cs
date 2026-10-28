using System.Text.Json;
using System.Text.Json.Nodes;
using Lidarr.AIImport.Companion.Approval;
using Lidarr.AIImport.Companion.Authorization;
using Lidarr.AIImport.Companion.Execution;
using Lidarr.AIImport.Companion.Filesystem;
using Lidarr.AIImport.Companion.Lidarr;

namespace Lidarr.AIImport.Companion.Tests;

[TestFixture]
public sealed class ImportExecutionServiceTests
{
    [Test]
    public async Task Revalidates_consumes_publishes_persists_then_submits()
    {
        using var temp = new TempDirectory();
        var store = new ApprovalStore(Path.Combine(temp.Path, "approvals.json"));
        var plan = Plan();
        var created = await store.CreateAsync(plan.AnalysisId, JsonSerializer.SerializeToElement(plan), TimeSpan.FromMinutes(15), CancellationToken.None);
        var order = new List<string>();
        var service = new ImportExecutionService(
            store,
            new FakeRevalidator(order),
            new FakeAuthorizations(order),
            new FakeExecutionClient(order));

        var result = await service.ExecuteAsync(created.ApprovalId, created.Token, CancellationToken.None);
        var record = await store.GetAsync(created.ApprovalId, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(result.CommandId, Is.EqualTo(42));
            Assert.That(order, Is.EqualTo(new[] { "revalidate", "publish", "submit" }));
            Assert.That(record!.State, Is.EqualTo(ApprovalState.Executing));
            Assert.That(record.AuthorizationIds, Is.EqualTo(new[] { "auth-1" }));
            Assert.That(record.AuthorizationBindings!.Single().SourceId, Is.EqualTo("source"));
            Assert.That(record.CommandId, Is.EqualTo(42));
            Assert.That(record.CommandStatus, Is.EqualTo("queued"));
        });
    }

    [Test]
    public async Task Uncertain_submission_becomes_indeterminate_and_is_never_retried()
    {
        using var temp = new TempDirectory();
        var store = new ApprovalStore(Path.Combine(temp.Path, "approvals.json"));
        var plan = Plan();
        var created = await store.CreateAsync(plan.AnalysisId, JsonSerializer.SerializeToElement(plan), TimeSpan.FromMinutes(15), CancellationToken.None);
        var service = new ImportExecutionService(
            store,
            new FakeRevalidator([]),
            new FakeAuthorizations([]),
            new FakeExecutionClient([], fail: true));

        Assert.ThrowsAsync<ImportOutcomeIndeterminateException>(() =>
            service.ExecuteAsync(created.ApprovalId, created.Token, CancellationToken.None));
        var record = await store.GetAsync(created.ApprovalId, CancellationToken.None);
        Assert.That(record!.State, Is.EqualTo(ApprovalState.Indeterminate));
        Assert.ThrowsAsync<ApprovalReplayException>(() =>
            service.ExecuteAsync(created.ApprovalId, created.Token, CancellationToken.None));
    }

    private static ApprovedImportPlan Plan() => new(
        "analysis", "download", 5, 10, 101, "safety",
        [new ApprovedImportFile("source", 77, "/downloads/track.flac", 1001,
            JsonNode.Parse("{\"quality\":{\"id\":1}}")!.AsObject(), 0, "download", 123,
            new FileIdentity(1, 2, 123, 4, 5, 8, 9, 420, 6, 7, 1))]);

    private sealed class FakeRevalidator(List<string> order) : IApprovedPlanRevalidator
    {
        public Task RevalidateAsync(ApprovedImportPlan expected, CancellationToken cancellationToken)
        {
            order.Add("revalidate");
            return Task.CompletedTask;
        }
    }

    private sealed class FakeAuthorizations(List<string> order) : ITransferAuthorizationStore
    {
        public Task<IReadOnlyList<PublishedTransferAuthorization>> PublishAsync(
            ApprovedImportPlan plan, DateTimeOffset expiresAt, CancellationToken cancellationToken)
        {
            order.Add("publish");
            return Task.FromResult<IReadOnlyList<PublishedTransferAuthorization>>(
                [new PublishedTransferAuthorization(
                    "auth-1", "/auth/auth-1.approved", "source", 1001,
                    "/downloads/track.flac", 1, 2)]);
        }
    }

    private sealed class FakeExecutionClient(List<string> order, bool fail = false) : ILidarrExecutionClient
    {
        public Task<string> GetArtistPathAsync(int artistId, CancellationToken cancellationToken) =>
            Task.FromResult("/music/Artist");
        public Task RevalidateManualImportAsync(ApprovedImportPlan plan, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<LidarrCommandReceipt> SubmitManualImportAsync(ApprovedImportPlan plan, CancellationToken cancellationToken)
        {
            order.Add("submit");
            return fail
                ? Task.FromException<LidarrCommandReceipt>(new HttpRequestException("response lost"))
                : Task.FromResult(new LidarrCommandReceipt(42, "queued"));
        }
    }

    private sealed class TempDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ai-import-execution-" + Guid.NewGuid().ToString("N"));
        public TempDirectory() => Directory.CreateDirectory(Path);
        public void Dispose() => Directory.Delete(Path, true);
    }
}
