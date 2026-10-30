using System.Text.Json;
using System.Text.Json.Nodes;
using Lidarr.AIImport.Companion.Approval;
using Lidarr.AIImport.Companion.Execution;
using Lidarr.AIImport.Companion.Filesystem;

namespace Lidarr.AIImport.Companion.Tests;

[TestFixture]
public sealed class ImportReconciliationCoordinatorTests
{
    [Test]
    public async Task Verified_reconciliation_durably_completes_an_execution()
    {
        using var temp = new TempDirectory();
        var store = new ApprovalStore(Path.Combine(temp.Path, "approvals.json"));
        var plan = Plan();
        var created = await store.CreateAsync(plan.AnalysisId, JsonSerializer.SerializeToElement(plan),
            TimeSpan.FromMinutes(15), CancellationToken.None);
        await store.BeginExecutionAsync(created.ApprovalId, created.Token, plan.AnalysisId, CancellationToken.None);
        await store.RecordAuthorizationBindingsAsync(created.ApprovalId, [Binding()], CancellationToken.None);
        await store.RecordCommandAsync(created.ApprovalId, 42, "queued", CancellationToken.None);
        var coordinator = new ImportReconciliationCoordinator(store, new FakeReconciliationService(
            new ImportReconciliationResult(ImportReconciliationStatus.Succeeded, 1, "verified")));

        var result = await coordinator.ReconcileAsync(
            created.ApprovalId, created.Token, CancellationToken.None);
        var record = await store.GetAsync(created.ApprovalId, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(result.Status, Is.EqualTo(ImportReconciliationStatus.Succeeded));
            Assert.That(record!.State, Is.EqualTo(ApprovalState.Succeeded));
            Assert.That(record.Outcome, Is.EqualTo("verified"));
        });
    }

    [Test]
    public async Task Lost_command_receipt_can_complete_only_through_verified_reconciliation()
    {
        using var temp = new TempDirectory();
        var store = new ApprovalStore(Path.Combine(temp.Path, "approvals.json"));
        var plan = Plan();
        var created = await store.CreateAsync(plan.AnalysisId, JsonSerializer.SerializeToElement(plan),
            TimeSpan.FromMinutes(15), CancellationToken.None);
        await store.BeginExecutionAsync(created.ApprovalId, created.Token, plan.AnalysisId, CancellationToken.None);
        await store.RecordAuthorizationBindingsAsync(created.ApprovalId, [Binding()], CancellationToken.None);
        await store.CompleteAsync(created.ApprovalId, ApprovalState.Indeterminate, "response lost", CancellationToken.None);
        var service = new FakeReconciliationService(
            new ImportReconciliationResult(ImportReconciliationStatus.Succeeded, 1, "verified without receipt"));
        var coordinator = new ImportReconciliationCoordinator(store, service);

        await coordinator.ReconcileAsync(created.ApprovalId, created.Token, CancellationToken.None);
        var record = await store.GetAsync(created.ApprovalId, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(service.CommandId, Is.Null);
            Assert.That(record!.State, Is.EqualTo(ApprovalState.Succeeded));
        });
    }

    private const string AuthId = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    private static ApprovalAuthorizationBinding Binding() =>
        new(AuthId, "source", 1001, "/downloads/track.flac", 1, 2);

    private static ApprovedImportPlan Plan() => new(
        "analysis", "download", 5, 10, 101, "safety",
        [new ApprovedImportFile("source", 77, "/downloads/track.flac", 1001,
            new JsonObject { ["quality"] = new JsonObject { ["id"] = 1 } }, 0, "download", 123,
            new FileIdentity(1, 2, 123, 4, 5, 8, 9, 420, 6, 7, 1))]);

    private sealed class FakeReconciliationService(ImportReconciliationResult result) : IImportReconciliationService
    {
        public int? CommandId { get; private set; }

        public Task<ImportReconciliationResult> ReconcileAsync(
            ApprovedImportPlan plan,
            IReadOnlyList<ApprovalAuthorizationBinding> authorizationBindings,
            int? commandId,
            DateTimeOffset executionStartedAt,
            CancellationToken cancellationToken)
        {
            CommandId = commandId;
            return Task.FromResult(result);
        }
    }

    private sealed class TempDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ai-import-reconcile-" + Guid.NewGuid().ToString("N"));
        public TempDirectory() => Directory.CreateDirectory(Path);
        public void Dispose() => Directory.Delete(Path, true);
    }
}
