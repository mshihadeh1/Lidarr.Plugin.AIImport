using System.Text.Json;
using System.Text.Json.Nodes;
using Lidarr.Plugin.AIImport.Approval;
using Lidarr.Plugin.AIImport.Filesystem;

namespace Lidarr.Plugin.AIImport.Tests;

[TestFixture]
public sealed class ApprovalServiceTests
{
    [Test]
    public async Task Approval_binds_exact_plan_and_revalidation_rejects_snapshot_tampering()
    {
        using var temp = new TempDirectory();
        var provider = new MutableProvider(Plan());
        var store = new ApprovalStore(Path.Combine(temp.Path, "approvals.json"));
        var service = new ApprovalService(provider, store);
        var approval = await service.ApproveAsync("analysis", TimeSpan.FromMinutes(15), CancellationToken.None);
        var record = await store.AuthenticateAsync(approval.ApprovalId, approval.Token, CancellationToken.None);
        var approved = record.Payload.Deserialize<ApprovedImportPlan>()!;

        await service.RevalidateAsync(approved, CancellationToken.None);
        provider.Plan = provider.Plan with { ReleaseId = 102 };

        Assert.ThrowsAsync<StaleApprovalException>(() => service.RevalidateAsync(approved, CancellationToken.None));
        var stillApproved = await store.AuthenticateAsync(approval.ApprovalId, approval.Token, CancellationToken.None);
        Assert.That(stillApproved.State, Is.EqualTo(ApprovalState.Approved));
    }

    private static ApprovedImportPlan Plan() => new(
        "analysis", "download", 5, 10, 101, "safety-fingerprint",
        [new ApprovedImportFile("source", 77, "/downloads/track.flac", 1001,
            JsonNode.Parse("{\"quality\":{\"id\":1}}")!.AsObject(), 0, "download", 123,
            new FileIdentity(1, 2, 123, 4, 5, 8, 9, 420, 6, 7, 1))], "/music/Artist");

    private sealed class MutableProvider(ApprovedImportPlan plan) : IApprovedPlanProvider
    {
        public ApprovedImportPlan Plan { get; set; } = plan;
        public Task<ApprovedImportPlan> BuildCurrentAsync(string analysisId, CancellationToken cancellationToken) => Task.FromResult(Plan);
    }

    private sealed class TempDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ai-import-approval-service-" + Guid.NewGuid().ToString("N"));
        public TempDirectory() => Directory.CreateDirectory(Path);
        public void Dispose() => Directory.Delete(Path, true);
    }
}
