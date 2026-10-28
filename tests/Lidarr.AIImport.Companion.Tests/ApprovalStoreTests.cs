using System.Text.Json;
using Lidarr.AIImport.Companion.Approval;

namespace Lidarr.AIImport.Companion.Tests;

[TestFixture]
public sealed class ApprovalStoreTests
{
    [Test]
    public async Task Waits_for_cross_process_lock_before_reading_state()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "approvals.json");
        var store = new ApprovalStore(path);
        var created = await store.CreateAsync("analysis", JsonDocument.Parse("{}").RootElement.Clone(),
            TimeSpan.FromMinutes(15), CancellationToken.None);
        await using var blocker = new FileStream(path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);

        var authentication = store.AuthenticateAsync(created.ApprovalId, created.Token, CancellationToken.None);
        await Task.Delay(100);
        Assert.That(authentication.IsCompleted, Is.False);

        await blocker.DisposeAsync();
        var record = await authentication;
        Assert.That(record.ApprovalId, Is.EqualTo(created.ApprovalId));
    }

    [Test]
    public async Task Authenticated_status_remains_available_after_terminal_success()
    {
        using var temp = new TempDirectory();
        var store = new ApprovalStore(Path.Combine(temp.Path, "approvals.json"));
        var created = await store.CreateAsync("analysis", JsonDocument.Parse("{}").RootElement.Clone(),
            TimeSpan.FromMinutes(15), CancellationToken.None);
        await store.BeginExecutionAsync(created.ApprovalId, created.Token, "analysis", CancellationToken.None);
        await store.CompleteAsync(created.ApprovalId, ApprovalState.Succeeded, "verified", CancellationToken.None);

        var status = await store.AuthenticateForStatusAsync(created.ApprovalId, created.Token, CancellationToken.None);

        Assert.That(status.State, Is.EqualTo(ApprovalState.Succeeded));
        Assert.ThrowsAsync<ApprovalAuthenticationException>(() =>
            store.AuthenticateForStatusAsync(created.ApprovalId, "wrong", CancellationToken.None));
    }

    [Test]
    public async Task Indeterminate_execution_can_be_authenticated_and_completed_only_by_verified_reconciliation()
    {
        using var temp = new TempDirectory();
        var store = new ApprovalStore(Path.Combine(temp.Path, "approvals.json"));
        var created = await store.CreateAsync("analysis", JsonDocument.Parse("{}").RootElement.Clone(),
            TimeSpan.FromMinutes(15), CancellationToken.None);
        await store.BeginExecutionAsync(created.ApprovalId, created.Token, "analysis", CancellationToken.None);
        await store.CompleteAsync(created.ApprovalId, ApprovalState.Indeterminate, "response lost", CancellationToken.None);

        var record = await store.AuthenticateForReconciliationAsync(
            created.ApprovalId, created.Token, CancellationToken.None);
        await store.CompleteVerifiedReconciliationAsync(created.ApprovalId, "verified", CancellationToken.None);
        var completed = await store.GetAsync(created.ApprovalId, CancellationToken.None);

        Assert.That(record.State, Is.EqualTo(ApprovalState.Indeterminate));
        Assert.That(completed!.State, Is.EqualTo(ApprovalState.Succeeded));
        Assert.ThrowsAsync<ApprovalReplayException>(() =>
            store.CompleteVerifiedReconciliationAsync(created.ApprovalId, "rewrite", CancellationToken.None));
    }

    [Test]
    public async Task Records_command_receipt_exactly_once_after_authorizations()
    {
        using var temp = new TempDirectory();
        var store = new ApprovalStore(Path.Combine(temp.Path, "approvals.json"));
        var created = await store.CreateAsync("analysis", JsonSerializer.SerializeToElement(new { value = 1 }),
            TimeSpan.FromMinutes(15), CancellationToken.None);
        await store.BeginExecutionAsync(created.ApprovalId, created.Token, "analysis", CancellationToken.None);
        await store.RecordAuthorizationBindingsAsync(created.ApprovalId, [Binding("auth-1", "source-1", 1)], CancellationToken.None);

        await store.RecordCommandAsync(created.ApprovalId, 42, "queued", CancellationToken.None);
        var record = await store.GetAsync(created.ApprovalId, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(record!.CommandId, Is.EqualTo(42));
            Assert.That(record.CommandStatus, Is.EqualTo("queued"));
            Assert.That(record.State, Is.EqualTo(ApprovalState.Executing));
        });
        Assert.ThrowsAsync<ApprovalReplayException>(() =>
            store.RecordCommandAsync(created.ApprovalId, 43, "queued", CancellationToken.None));
    }

    [Test]
    public async Task Persists_approval_and_allows_exactly_one_execution_start()
    {
        using var temp = new TempDirectory();
        var clock = new FixedTimeProvider(DateTimeOffset.Parse("2026-10-27T08:00:00Z"));
        var path = Path.Combine(temp.Path, "approvals.json");
        var created = await new ApprovalStore(path, clock).CreateAsync(
            "analysis-1", JsonDocument.Parse("{\"releaseId\":101}").RootElement.Clone(), TimeSpan.FromMinutes(15), CancellationToken.None);

        var reloaded = new ApprovalStore(path, clock);
        var begun = await reloaded.BeginExecutionAsync(created.ApprovalId, created.Token, "analysis-1", CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(begun.State, Is.EqualTo(ApprovalState.Executing));
            Assert.That(begun.Payload.GetProperty("releaseId").GetInt32(), Is.EqualTo(101));
            if (!OperatingSystem.IsWindows())
                Assert.That(File.GetUnixFileMode(path), Is.EqualTo(UnixFileMode.UserRead | UnixFileMode.UserWrite));
        });
        Assert.ThrowsAsync<ApprovalReplayException>(() =>
            reloaded.BeginExecutionAsync(created.ApprovalId, created.Token, "analysis-1", CancellationToken.None));
    }

    [Test]
    public async Task Wrong_token_and_stale_analysis_fail_without_consuming_approval()
    {
        using var temp = new TempDirectory();
        var clock = new FixedTimeProvider(DateTimeOffset.Parse("2026-10-27T08:00:00Z"));
        var store = new ApprovalStore(Path.Combine(temp.Path, "approvals.json"), clock);
        var created = await store.CreateAsync(
            "analysis-1", JsonDocument.Parse("{}").RootElement.Clone(), TimeSpan.FromMinutes(15), CancellationToken.None);

        Assert.ThrowsAsync<ApprovalAuthenticationException>(() =>
            store.BeginExecutionAsync(created.ApprovalId, "wrong", "analysis-1", CancellationToken.None));
        Assert.ThrowsAsync<StaleApprovalException>(() =>
            store.BeginExecutionAsync(created.ApprovalId, created.Token, "analysis-2", CancellationToken.None));

        var begun = await store.BeginExecutionAsync(created.ApprovalId, created.Token, "analysis-1", CancellationToken.None);
        Assert.That(begun.State, Is.EqualTo(ApprovalState.Executing));
    }

    [Test]
    public async Task Expired_approval_fails_closed()
    {
        using var temp = new TempDirectory();
        var clock = new FixedTimeProvider(DateTimeOffset.Parse("2026-10-27T08:00:00Z"));
        var store = new ApprovalStore(Path.Combine(temp.Path, "approvals.json"), clock);
        var created = await store.CreateAsync(
            "analysis-1", JsonDocument.Parse("{}").RootElement.Clone(), TimeSpan.FromMinutes(1), CancellationToken.None);
        clock.UtcNow = clock.UtcNow.AddMinutes(2);

        Assert.ThrowsAsync<ApprovalExpiredException>(() =>
            store.BeginExecutionAsync(created.ApprovalId, created.Token, "analysis-1", CancellationToken.None));
    }

    [Test]
    public async Task Completion_is_durable_and_cannot_be_rewritten()
    {
        using var temp = new TempDirectory();
        var clock = new FixedTimeProvider(DateTimeOffset.Parse("2026-10-27T08:00:00Z"));
        var path = Path.Combine(temp.Path, "approvals.json");
        var store = new ApprovalStore(path, clock);
        var created = await store.CreateAsync(
            "analysis-1", JsonDocument.Parse("{}").RootElement.Clone(), TimeSpan.FromMinutes(15), CancellationToken.None);
        await store.BeginExecutionAsync(created.ApprovalId, created.Token, "analysis-1", CancellationToken.None);
        await store.CompleteAsync(created.ApprovalId, ApprovalState.Succeeded, "verified", CancellationToken.None);

        var record = await new ApprovalStore(path, clock).GetAsync(created.ApprovalId, CancellationToken.None);
        Assert.That(record!.State, Is.EqualTo(ApprovalState.Succeeded));
        Assert.ThrowsAsync<ApprovalReplayException>(() =>
            store.CompleteAsync(created.ApprovalId, ApprovalState.Failed, "rewrite", CancellationToken.None));
    }

    [Test]
    public async Task Startup_recovery_marks_orphaned_execution_indeterminate_without_retrying()
    {
        using var temp = new TempDirectory();
        var clock = new FixedTimeProvider(DateTimeOffset.Parse("2026-10-27T08:00:00Z"));
        var path = Path.Combine(temp.Path, "approvals.json");
        var store = new ApprovalStore(path, clock);
        var created = await store.CreateAsync(
            "analysis-1", JsonDocument.Parse("{}").RootElement.Clone(), TimeSpan.FromMinutes(15), CancellationToken.None);
        await store.BeginExecutionAsync(created.ApprovalId, created.Token, "analysis-1", CancellationToken.None);

        var recovered = await new ApprovalStore(path, clock).RecoverInterruptedExecutionsAsync(CancellationToken.None);
        var record = await store.GetAsync(created.ApprovalId, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(recovered, Is.EqualTo(1));
            Assert.That(record!.State, Is.EqualTo(ApprovalState.Indeterminate));
            Assert.That(record.Outcome, Does.Contain("restart"));
        });
    }

    [Test]
    public async Task Persists_native_authorization_ids_before_command_submission()
    {
        using var temp = new TempDirectory();
        var clock = new FixedTimeProvider(DateTimeOffset.Parse("2026-10-27T08:00:00Z"));
        var path = Path.Combine(temp.Path, "approvals.json");
        var store = new ApprovalStore(path, clock);
        var created = await store.CreateAsync(
            "analysis-1", JsonDocument.Parse("{}").RootElement.Clone(), TimeSpan.FromMinutes(15), CancellationToken.None);
        await store.BeginExecutionAsync(created.ApprovalId, created.Token, "analysis-1", CancellationToken.None);

        await store.RecordAuthorizationBindingsAsync(created.ApprovalId,
            [Binding("abc", "source-1", 1), Binding("def", "source-2", 2)], CancellationToken.None);
        var record = await new ApprovalStore(path, clock).GetAsync(created.ApprovalId, CancellationToken.None);

        Assert.That(record!.AuthorizationIds, Is.EqualTo(new[] { "abc", "def" }));
        Assert.That(record.AuthorizationBindings!.Select(binding => binding.SourceId),
            Is.EqualTo(new[] { "source-1", "source-2" }));
        Assert.ThrowsAsync<ApprovalReplayException>(() =>
            store.RecordAuthorizationBindingsAsync(created.ApprovalId,
                [Binding("replacement", "source-3", 3)], CancellationToken.None));
    }

    [Test]
    public async Task Authentication_reads_payload_without_consuming_approval()
    {
        using var temp = new TempDirectory();
        var clock = new FixedTimeProvider(DateTimeOffset.Parse("2026-10-27T08:00:00Z"));
        var store = new ApprovalStore(Path.Combine(temp.Path, "approvals.json"), clock);
        var created = await store.CreateAsync(
            "analysis-1", JsonDocument.Parse("{\"value\":7}").RootElement.Clone(), TimeSpan.FromMinutes(15), CancellationToken.None);

        var authenticated = await store.AuthenticateAsync(created.ApprovalId, created.Token, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(authenticated.State, Is.EqualTo(ApprovalState.Approved));
            Assert.That(authenticated.Payload.GetProperty("value").GetInt32(), Is.EqualTo(7));
        });
        Assert.ThrowsAsync<ApprovalAuthenticationException>(() =>
            store.AuthenticateAsync(created.ApprovalId, "wrong", CancellationToken.None));
        var begun = await store.BeginExecutionAsync(created.ApprovalId, created.Token, "analysis-1", CancellationToken.None);
        Assert.That(begun.State, Is.EqualTo(ApprovalState.Executing));
    }

    private static ApprovalAuthorizationBinding Binding(string authorizationId, string sourceId, int trackId) =>
        new(authorizationId, sourceId, trackId, $"/downloads/{sourceId}.flac", 1, (ulong)trackId);

    private sealed class FixedTimeProvider(DateTimeOffset value) : TimeProvider
    {
        public DateTimeOffset UtcNow { get; set; } = value;
        public override DateTimeOffset GetUtcNow() => UtcNow;
    }

    private sealed class TempDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ai-import-approval-" + Guid.NewGuid().ToString("N"));
        public TempDirectory() => Directory.CreateDirectory(Path);
        public void Dispose() => Directory.Delete(Path, true);
    }
}
