using Lidarr.Plugin.AIImport.Commands;
using Lidarr.Plugin.AIImport.Configuration;
using Lidarr.Plugin.AIImport.Workflows;
using NzbDrone.Core.Messaging.Commands;

namespace Lidarr.Plugin.AIImport.Tests;

[TestFixture]
public sealed class AiImportDiscoverCommandTests
{
    [Test]
    public void Command_is_safe_read_only_and_publishes_updates()
    {
        var command = new AiImportDiscoverCommand();

        Assert.Multiple(() =>
        {
            Assert.That(command.SendUpdatesToClient, Is.True);
            Assert.That(command.UpdateScheduledTask, Is.False);
            Assert.That(command.RequiresDiskAccess, Is.False);
            Assert.That(command.IsExclusive, Is.False);
            Assert.That(command.IsLongRunning, Is.False);
        });
    }

    [Test]
    public void Handler_resolves_saved_provider_by_exact_id_and_delegates_to_workflow()
    {
        var settings = new AiImportSettings { ApiBaseUrl = "https://example.test/v1", Model = "model" };
        var result = new AiImportDiscoverResult([]);
        var workflow = new StubDiscoverWorkflow(result);
        var resolver = new StubResolver(settings);
        var command = new AiImportDiscoverCommand { ProviderDefinitionId = 27 };

        new AiImportDiscoverCommandHandler(workflow, resolver).Execute(command);

        Assert.Multiple(() =>
        {
            Assert.That(resolver.RequestedId, Is.EqualTo(27));
            Assert.That(workflow.Settings, Is.SameAs(settings));
            Assert.That(command.Status, Is.EqualTo(AiImportDiscoverStatus.Succeeded));
            Assert.That(command.Result, Is.SameAs(result));
        });
    }

    [Test]
    public void Command_and_handler_are_discoverable_by_lidarr_command_infrastructure()
    {
        Assert.Multiple(() =>
        {
            Assert.That(typeof(AiImportDiscoverCommand).IsSubclassOf(typeof(Command)), Is.True);
            Assert.That(typeof(IExecute<AiImportDiscoverCommand>).IsAssignableFrom(typeof(AiImportDiscoverCommandHandler)), Is.True);
        });
    }

    [Test]
    public void Handler_persists_server_side_result_for_a_disconnected_browser()
    {
        using var temp = new TestTempDirectory();
        var result = new AiImportDiscoverResult([]);
        var path = Path.Combine(temp.Path, "discovery-jobs.json");
        var store = new DiscoveryJobStore(path);
        var job = store.CreateOrGetActive(27).Job;
        var command = new AiImportDiscoverCommand { ProviderDefinitionId = 27, JobId = job.JobId };

        new AiImportDiscoverCommandHandler(
            new StubDiscoverWorkflow(result),
            new StubResolver(new AiImportSettings { ApiBaseUrl = "https://example.test/v1", Model = "model" }),
            store).Execute(command);

        var saved = new DiscoveryJobStore(path).GetLatest(27);
        Assert.Multiple(() =>
        {
            Assert.That(saved, Is.Not.Null);
            Assert.That(saved!.State, Is.EqualTo(DiscoveryJobState.Succeeded));
            Assert.That(saved.Result, Is.Not.Null);
            Assert.That(saved.Result!.CandidateCount, Is.Zero);
            Assert.That(saved.Result.Diagnostics, Is.Empty);
            Assert.That(saved.Progress!.Stage, Is.EqualTo("complete"));
            Assert.That(saved.Progress.OverallPercent, Is.EqualTo(100));
        });
    }

    [Test]
    public void Handler_persists_intermediate_progress_while_browser_is_disconnected()
    {
        using var temp = new TestTempDirectory();
        var path = Path.Combine(temp.Path, "discovery-jobs.json");
        var store = new DiscoveryJobStore(path);
        var job = store.CreateOrGetActive(27).Job;
        var workflow = new ProgressWorkflow(store, 27);

        new AiImportDiscoverCommandHandler(
            workflow,
            new StubResolver(new AiImportSettings { ApiBaseUrl = "https://example.test/v1", Model = "model" }),
            store).Execute(new AiImportDiscoverCommand { ProviderDefinitionId = 27, JobId = job.JobId });

        Assert.That(workflow.ObservedPersistedProgress, Is.EqualTo(64));
    }

    [Test]
    public void Handler_honors_a_durable_stop_before_entering_the_workflow()
    {
        using var temp = new TestTempDirectory();
        var path = Path.Combine(temp.Path, "discovery-jobs.json");
        var store = new DiscoveryJobStore(path);
        var job = store.CreateOrGetActive(27).Job;
        store.RequestStop(job.JobId);
        var workflow = new StubDiscoverWorkflow(new AiImportDiscoverResult([]));
        var command = new AiImportDiscoverCommand { ProviderDefinitionId = 27, JobId = job.JobId };

        new AiImportDiscoverCommandHandler(
            workflow,
            new StubResolver(new AiImportSettings { ApiBaseUrl = "https://example.test/v1", Model = "model" }),
            store).Execute(command);

        var saved = store.GetLatest(27);
        Assert.Multiple(() =>
        {
            Assert.That(command.Status, Is.EqualTo(AiImportDiscoverStatus.Stopped));
            Assert.That(command.Result, Is.Null);
            Assert.That(workflow.Settings, Is.Null);
            Assert.That(saved!.State, Is.EqualTo(DiscoveryJobState.Stopped));
        });
    }

    [Test]
    public void Handler_honors_a_stop_requested_at_a_workflow_checkpoint()
    {
        using var temp = new TestTempDirectory();
        var store = new DiscoveryJobStore(Path.Combine(temp.Path, "discovery-jobs.json"));
        var job = store.CreateOrGetActive(27).Job;
        var workflow = new StopInsideWorkflow(store, job.JobId);
        var command = new AiImportDiscoverCommand { ProviderDefinitionId = 27, JobId = job.JobId };

        new AiImportDiscoverCommandHandler(
            workflow,
            new StubResolver(new AiImportSettings { ApiBaseUrl = "https://example.test/v1", Model = "model" }),
            store).Execute(command);

        Assert.Multiple(() =>
        {
            Assert.That(workflow.ObservedStop, Is.True);
            Assert.That(command.Status, Is.EqualTo(AiImportDiscoverStatus.Stopped));
            Assert.That(store.GetLatest(27)!.State, Is.EqualTo(DiscoveryJobState.Stopped));
        });
    }

    [Test]
    public void Handler_marks_an_unresolved_submitted_import_indeterminate_without_a_stop_request()
    {
        using var temp = new TestTempDirectory();
        var store = new DiscoveryJobStore(Path.Combine(temp.Path, "discovery-jobs.json"));
        var job = store.CreateOrGetActive(27).Job;
        var command = new AiImportDiscoverCommand { ProviderDefinitionId = 27, JobId = job.JobId };

        new AiImportDiscoverCommandHandler(
            new StubDiscoverWorkflow(new AiImportDiscoverResult([], [], true, UnresolvedMutation: true)),
            new StubResolver(new AiImportSettings { ApiBaseUrl = "https://example.test/v1", Model = "model" }),
            store).Execute(command);

        Assert.Multiple(() =>
        {
            Assert.That(command.Status, Is.EqualTo(AiImportDiscoverStatus.Indeterminate));
            Assert.That(store.GetLatest(27)!.State, Is.EqualTo(DiscoveryJobState.Indeterminate));
            Assert.That(store.GetLatest(27)!.Error, Does.Contain("reconciliation is not yet verified"));
        });
    }

    [Test]
    public void Handler_does_not_mask_an_unresolved_submitted_import_as_stopped()
    {
        using var temp = new TestTempDirectory();
        var store = new DiscoveryJobStore(Path.Combine(temp.Path, "discovery-jobs.json"));
        var job = store.CreateOrGetActive(27).Job;
        var command = new AiImportDiscoverCommand { ProviderDefinitionId = 27, JobId = job.JobId };

        new AiImportDiscoverCommandHandler(
            new UnresolvedStopWorkflow(store, job.JobId),
            new StubResolver(new AiImportSettings { ApiBaseUrl = "https://example.test/v1", Model = "model" }),
            store).Execute(command);

        Assert.Multiple(() =>
        {
            Assert.That(command.Status, Is.EqualTo(AiImportDiscoverStatus.Indeterminate));
            Assert.That(store.GetLatest(27)!.State, Is.EqualTo(DiscoveryJobState.Indeterminate));
            Assert.That(store.GetLatest(27)!.Error, Does.Contain("reconciliation is not yet verified"));
        });
    }

    private sealed class UnresolvedStopWorkflow(DiscoveryJobStore store, string jobId) : IAiImportDiscoverWorkflow
    {
        public AiImportDiscoverResult Discover(
            AiImportSettings settings,
            Action<AiImportJobProgress>? progress = null,
            Func<bool>? stopRequested = null)
        {
            store.RequestStop(jobId);
            return new AiImportDiscoverResult([], [], true, UnresolvedMutation: true);
        }
    }

    private sealed class StopInsideWorkflow(DiscoveryJobStore store, string jobId) : IAiImportDiscoverWorkflow
    {
        public bool ObservedStop { get; private set; }

        public AiImportDiscoverResult Discover(
            AiImportSettings settings,
            Action<AiImportJobProgress>? progress = null,
            Func<bool>? stopRequested = null)
        {
            store.RequestStop(jobId);
            ObservedStop = stopRequested?.Invoke() == true;
            if (ObservedStop) throw new AiImportStopRequestedException();
            return new AiImportDiscoverResult([]);
        }
    }

    private sealed class StubDiscoverWorkflow(AiImportDiscoverResult result) : IAiImportDiscoverWorkflow
    {
        public AiImportSettings? Settings { get; private set; }
        public AiImportDiscoverResult Discover(AiImportSettings settings, Action<AiImportJobProgress>? progress = null, Func<bool>? stopRequested = null)
        {
            Settings = settings;
            return result;
        }
    }

    private sealed class ProgressWorkflow(DiscoveryJobStore store, int providerId) : IAiImportDiscoverWorkflow
    {
        public int ObservedPersistedProgress { get; private set; }

        public AiImportDiscoverResult Discover(AiImportSettings settings, Action<AiImportJobProgress>? progress = null, Func<bool>? stopRequested = null)
        {
            progress?.Invoke(new AiImportJobProgress("matching", "Matching album", 64, "Album", 20, 0, 8, 0));
            ObservedPersistedProgress = store.GetLatest(providerId)?.Progress?.OverallPercent ?? -1;
            return new AiImportDiscoverResult([]);
        }
    }

    private sealed class StubResolver(AiImportSettings settings) : IAiImportSettingsResolver
    {
        public int RequestedId { get; private set; }
        public AiImportSettings ResolveEnabled(int requestedDefinitionId)
        {
            RequestedId = requestedDefinitionId;
            return settings;
        }
    }
}
