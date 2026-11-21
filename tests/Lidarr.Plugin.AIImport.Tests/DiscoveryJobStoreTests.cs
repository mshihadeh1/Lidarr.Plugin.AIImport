using Lidarr.Plugin.AIImport.Commands;
using Lidarr.Plugin.AIImport.Configuration;
using Lidarr.Plugin.AIImport.Discovery;
using Lidarr.Plugin.AIImport.Matching;
using Lidarr.Plugin.AIImport.Workflows;
using NzbDrone.Core.Messaging.Commands;
using System.Text.Json.Nodes;

namespace Lidarr.Plugin.AIImport.Tests;

[TestFixture]
public sealed class DiscoveryJobStoreTests
{
    [Test]
    public void Active_job_is_deduplicated_and_survives_a_new_store_instance()
    {
        using var temp = new TestTempDirectory();
        var path = Path.Combine(temp.Path, "discovery-jobs.json");
        var store = new DiscoveryJobStore(path);

        var first = store.CreateOrGetActive(27);
        var second = store.CreateOrGetActive(27);
        store.AttachCommand(first.Job.JobId, 42);

        var reloaded = new DiscoveryJobStore(path).GetLatest(27);
        Assert.Multiple(() =>
        {
            Assert.That(first.Created, Is.True);
            Assert.That(second.Created, Is.False);
            Assert.That(second.Job.JobId, Is.EqualTo(first.Job.JobId));
            Assert.That(reloaded, Is.Not.Null);
            Assert.That(reloaded!.CommandId, Is.EqualTo(42));
            Assert.That(reloaded.State, Is.EqualTo(DiscoveryJobState.Queued));
        });
    }

    [Test]
    public void Completed_result_is_available_after_browser_independent_reload()
    {
        using var temp = new TestTempDirectory();
        var path = Path.Combine(temp.Path, "discovery-jobs.json");
        var store = new DiscoveryJobStore(path);
        var job = store.CreateOrGetActive(27).Job;
        var result = Result();

        store.MarkRunning(job.JobId);
        store.Complete(job.JobId, result);

        var reloaded = new DiscoveryJobStore(path).GetLatest(27);
        Assert.Multiple(() =>
        {
            Assert.That(reloaded, Is.Not.Null);
            Assert.That(reloaded!.State, Is.EqualTo(DiscoveryJobState.Succeeded));
            Assert.That(reloaded.Result, Is.Not.Null);
            Assert.That(reloaded.Result!.CandidateCount, Is.EqualTo(1));
            Assert.That(reloaded.Result.Analyses.Single().AnalysisId, Is.EqualTo("analysis-1"));
            Assert.That(reloaded.Result.Diagnostics, Is.Empty);
            Assert.That(reloaded.CompletedAt, Is.Not.Null);
            Assert.That(File.ReadAllText(path), Does.Contain("\"State\": \"Succeeded\""));
        });
    }

    [Test]
    public void Running_progress_is_durable_and_reports_current_album_and_files()
    {
        using var temp = new TestTempDirectory();
        var path = Path.Combine(temp.Path, "discovery-jobs.json");
        var store = new DiscoveryJobStore(path);
        var job = store.CreateOrGetActive(27).Job;

        store.UpdateProgress(job.JobId, new AiImportJobProgress(
            "importing", "Importing Artist — Album", 72,
            "Artist — Album", 45, 3, 10, 1));

        var saved = new DiscoveryJobStore(path).GetLatest(27);
        Assert.Multiple(() =>
        {
            Assert.That(saved!.Progress!.Stage, Is.EqualTo("importing"));
            Assert.That(saved.Progress.OverallPercent, Is.EqualTo(72));
            Assert.That(saved.Progress.AlbumPercent, Is.EqualTo(45));
            Assert.That(saved.Progress.CompletedFiles, Is.EqualTo(3));
            Assert.That(saved.Progress.TotalFiles, Is.EqualTo(10));
            Assert.That(saved.Progress.CompletedAlbums, Is.EqualTo(1));
            Assert.That(saved.Progress.UpdatedAt, Is.Not.EqualTo(default(DateTimeOffset)));
        });
    }

    [Test]
    public void Fresh_discovery_pass_resets_stage_progress_without_erasing_completed_album_count()
    {
        using var temp = new TestTempDirectory();
        var store = new DiscoveryJobStore(Path.Combine(temp.Path, "discovery-jobs.json"));
        var job = store.CreateOrGetActive(27).Job;
        store.UpdateProgress(job.JobId, new AiImportJobProgress(
            "album-complete", "Imported album", 94, "Album one", 100, 10, 10, 1));

        var nextPass = store.UpdateProgress(job.JobId, new AiImportJobProgress(
            "scanning-queue", "Scanning a fresh Lidarr snapshot", 10, CompletedAlbums: 1));

        Assert.Multiple(() =>
        {
            Assert.That(nextPass.Progress!.Stage, Is.EqualTo("scanning-queue"));
            Assert.That(nextPass.Progress.OverallPercent, Is.EqualTo(10));
            Assert.That(nextPass.Progress.CompletedAlbums, Is.EqualTo(1));
        });
    }

    [Test]
    public void Stop_request_is_durable_and_job_finishes_as_stopped_without_result()
    {
        using var temp = new TestTempDirectory();
        var path = Path.Combine(temp.Path, "discovery-jobs.json");
        var store = new DiscoveryJobStore(path);
        var job = store.CreateOrGetActive(27).Job;
        store.MarkRunning(job.JobId);

        var requested = store.RequestStop(job.JobId);
        var visibleToWorker = new DiscoveryJobStore(path).IsStopRequested(job.JobId);
        var stopped = new DiscoveryJobStore(path).MarkStopped(job.JobId,
            "Stopped before starting another AI Import operation.");

        Assert.Multiple(() =>
        {
            Assert.That(requested.StopRequestedAt, Is.Not.Null);
            Assert.That(visibleToWorker, Is.True);
            Assert.That(stopped.State, Is.EqualTo(DiscoveryJobState.Stopped));
            Assert.That(stopped.Result, Is.Null);
            Assert.That(stopped.Progress!.Stage, Is.EqualTo("stopped"));
            Assert.That(stopped.CompletedAt, Is.Not.Null);
        });
    }

    [Test]
    public void Stop_request_is_persisted_without_reconciling_a_transiently_missing_command()
    {
        using var temp = new TestTempDirectory();
        var store = new DiscoveryJobStore(Path.Combine(temp.Path, "discovery-jobs.json"));
        var submitter = new StubSubmitter();
        var service = new AiImportDiscoveryJobService(store, submitter, new StubResolver());
        var job = service.Start(27);
        submitter.Models.Clear();

        var stopped = service.RequestStop(27);

        Assert.Multiple(() =>
        {
            Assert.That(stopped.JobId, Is.EqualTo(job.JobId));
            Assert.That(stopped.State, Is.EqualTo(DiscoveryJobState.Queued));
            Assert.That(stopped.StopRequestedAt, Is.Not.Null);
        });
    }

    [Test]
    public void Mutation_admission_orders_a_current_operation_before_a_durable_stop()
    {
        using var temp = new TestTempDirectory();
        var store = new DiscoveryJobStore(Path.Combine(temp.Path, "discovery-jobs.json"));
        var job = store.CreateOrGetActive(27).Job;
        using var current = AiImportMutationAdmission.Admit(() => store.IsStopRequested(job.JobId));

        AiImportMutationAdmission.PersistStop(() => store.RequestStop(job.JobId));

        Assert.That(
            () => AiImportMutationAdmission.Admit(() => store.IsStopRequested(job.JobId)),
            Throws.TypeOf<AiImportStopRequestedException>());
    }

    [Test]
    public void Long_terminal_error_is_persisted_with_bounded_progress_text()
    {
        using var temp = new TestTempDirectory();
        var store = new DiscoveryJobStore(Path.Combine(temp.Path, "discovery-jobs.json"));
        var job = store.CreateOrGetActive(27).Job;

        store.MarkIndeterminate(job.JobId, new string('x', 450));
        var saved = store.GetLatest(27)!;

        Assert.Multiple(() =>
        {
            Assert.That(saved.State, Is.EqualTo(DiscoveryJobState.Indeterminate));
            Assert.That(saved.Error, Has.Length.EqualTo(450));
            Assert.That(saved.Progress!.Message, Has.Length.EqualTo(300));
        });
    }

    [Test]
    public void V206_record_without_progress_or_stop_timestamp_remains_readable()
    {
        using var temp = new TestTempDirectory();
        var path = Path.Combine(temp.Path, "discovery-jobs.json");
        var store = new DiscoveryJobStore(path);
        store.CreateOrGetActive(27);
        var array = JsonNode.Parse(File.ReadAllText(path))!.AsArray();
        var record = array[0]!.AsObject();
        record.Remove("Progress");
        record.Remove("StopRequestedAt");
        File.WriteAllText(path, array.ToJsonString());

        var loaded = new DiscoveryJobStore(path).GetLatest(27);

        Assert.Multiple(() =>
        {
            Assert.That(loaded, Is.Not.Null);
            Assert.That(loaded!.Progress, Is.Null);
            Assert.That(loaded.StopRequestedAt, Is.Null);
        });
    }

    [Test]
    public void Stopped_job_blocks_automatic_start_until_explicit_resume()
    {
        using var temp = new TestTempDirectory();
        var store = new DiscoveryJobStore(Path.Combine(temp.Path, "discovery-jobs.json"));
        var submitter = new StubSubmitter();
        var service = new AiImportDiscoveryJobService(store, submitter, new StubResolver());
        var first = service.Start(27);
        service.RequestStop(27);
        store.MarkStopped(first.JobId, "Stopped by operator.");

        var blocked = service.Start(27);
        var resumed = service.RestartAfterStopped(27);

        Assert.Multiple(() =>
        {
            Assert.That(blocked.JobId, Is.EqualTo(first.JobId));
            Assert.That(blocked.State, Is.EqualTo(DiscoveryJobState.Stopped));
            Assert.That(resumed.JobId, Is.Not.EqualTo(first.JobId));
            Assert.That(submitter.Commands, Has.Count.EqualTo(2));
        });
    }

    [Test]
    public void Service_submits_once_and_another_client_observes_the_same_job()
    {
        using var temp = new TestTempDirectory();
        var path = Path.Combine(temp.Path, "discovery-jobs.json");
        var submitter = new StubSubmitter();
        var resolver = new StubResolver();
        var service = new AiImportDiscoveryJobService(new DiscoveryJobStore(path), submitter, resolver);

        var first = service.Start(27);
        var second = service.Start(27);
        var fromAnotherClient = new AiImportDiscoveryJobService(
            new DiscoveryJobStore(path), submitter, resolver).GetLatest(27);

        Assert.Multiple(() =>
        {
            Assert.That(first.JobId, Is.EqualTo(second.JobId));
            Assert.That(first.CommandId, Is.EqualTo(42));
            Assert.That(submitter.Commands, Has.Count.EqualTo(1));
            Assert.That(submitter.Commands.Single().JobId, Is.EqualTo(first.JobId));
            Assert.That(fromAnotherClient!.JobId, Is.EqualTo(first.JobId));
            Assert.That(resolver.RequestedIds, Has.All.EqualTo(27));
        });
    }

    [Test]
    public void Orphaned_command_becomes_indeterminate_and_requires_explicit_restart()
    {
        using var temp = new TestTempDirectory();
        var submitter = new StubSubmitter();
        var service = new AiImportDiscoveryJobService(
            new DiscoveryJobStore(Path.Combine(temp.Path, "discovery-jobs.json")), submitter, new StubResolver());
        var first = service.Start(27);
        submitter.Models[first.CommandId!.Value].Status = CommandStatus.Orphaned;

        var reconciled = service.GetLatest(27);
        var blocked = service.Start(27);
        var restarted = service.RestartAfterIndeterminate(27);

        Assert.Multiple(() =>
        {
            Assert.That(reconciled!.State, Is.EqualTo(DiscoveryJobState.Indeterminate));
            Assert.That(blocked.JobId, Is.EqualTo(first.JobId));
            Assert.That(restarted.JobId, Is.Not.EqualTo(first.JobId));
            Assert.That(submitter.Commands, Has.Count.EqualTo(2));
        });
    }

    [Test]
    public void Receiptless_queued_record_is_indeterminate_until_explicitly_acknowledged()
    {
        using var temp = new TestTempDirectory();
        var path = Path.Combine(temp.Path, "discovery-jobs.json");
        var abandoned = new DiscoveryJobStore(path).CreateOrGetActive(27).Job;
        var submitter = new StubSubmitter();
        var service = new AiImportDiscoveryJobService(new DiscoveryJobStore(path), submitter, new StubResolver());

        var blocked = service.Start(27);
        var replacement = service.RestartAfterIndeterminate(27);

        Assert.Multiple(() =>
        {
            Assert.That(blocked.JobId, Is.EqualTo(abandoned.JobId));
            Assert.That(blocked.State, Is.EqualTo(DiscoveryJobState.Indeterminate));
            Assert.That(blocked.Error, Does.Contain("automatic retry is forbidden"));
            Assert.That(replacement.JobId, Is.Not.EqualTo(abandoned.JobId));
            Assert.That(replacement.CommandId, Is.EqualTo(42));
            Assert.That(submitter.Commands, Has.Count.EqualTo(1));
        });
    }

    [Test]
    public void Different_store_instances_share_the_same_transaction_gate()
    {
        using var temp = new TestTempDirectory();
        var path = Path.Combine(temp.Path, "discovery-jobs.json");
        var first = new DiscoveryJobStore(path);
        var second = new DiscoveryJobStore(path);
        var job = first.CreateOrGetActive(27).Job;

        first.AttachCommand(job.JobId, 42);
        second.MarkRunning(job.JobId);
        first.Complete(job.JobId, Result());

        var saved = second.GetLatest(27);
        Assert.Multiple(() =>
        {
            Assert.That(saved!.CommandId, Is.EqualTo(42));
            Assert.That(saved.State, Is.EqualTo(DiscoveryJobState.Succeeded));
        });
    }

    [Test]
    public void Malformed_stopped_record_without_a_stop_request_fails_closed()
    {
        using var temp = new TestTempDirectory();
        var path = Path.Combine(temp.Path, "discovery-jobs.json");
        var store = new DiscoveryJobStore(path);
        var job = store.CreateOrGetActive(27).Job;
        store.RequestStop(job.JobId);
        store.MarkStopped(job.JobId, "Stopped.");
        var array = JsonNode.Parse(File.ReadAllText(path))!.AsArray();
        array[0]!.AsObject().Remove("StopRequestedAt");
        File.WriteAllText(path, array.ToJsonString());

        Assert.That(() => new DiscoveryJobStore(path).GetLatest(27),
            Throws.TypeOf<InvalidDataException>());
    }

    [Test]
    public void Malformed_progress_timestamp_fails_closed()
    {
        using var temp = new TestTempDirectory();
        var path = Path.Combine(temp.Path, "discovery-jobs.json");
        new DiscoveryJobStore(path).CreateOrGetActive(27);
        var array = JsonNode.Parse(File.ReadAllText(path))!.AsArray();
        array[0]!["Progress"]!["UpdatedAt"] = DateTimeOffset.MinValue;
        File.WriteAllText(path, array.ToJsonString());

        Assert.That(() => new DiscoveryJobStore(path).GetLatest(27),
            Throws.TypeOf<InvalidDataException>());
    }

    [Test]
    public void Malformed_null_record_fails_closed()
    {
        using var temp = new TestTempDirectory();
        var path = Path.Combine(temp.Path, "discovery-jobs.json");
        File.WriteAllText(path, "[null]");

        Assert.That(() => new DiscoveryJobStore(path).GetLatest(27),
            Throws.TypeOf<InvalidDataException>());
    }

    private static AiImportDiscoverResult Result()
    {
        var proposal = new MatchProposal(
            1, ProposalStatus.Proposed, 5, 10, 101, 0.98m, "verified",
            [new TrackProposal("source-1", 1001, 0.99m)]);
        return new AiImportDiscoverResult([
            new AiImportAnalysisDto(
                "analysis-1", "download-1", "Artist - Album", proposal,
                [new SourceFileDto("source-1", "01 - Song.flac", "Artist", "Album", "Song", 1, [1], 180000)],
                [new ReleaseCandidateDto(10, 101, 5, "Artist", "Album", "release-id",
                    [new ReleaseTrackDto(1001, "Song", 1, "1", 180000, false)])],
                false)
        ]);
    }

    private sealed class StubSubmitter : IDiscoveryCommandSubmitter
    {
        public List<AiImportDiscoverCommand> Commands { get; } = [];
        public Dictionary<int, CommandModel> Models { get; } = [];
        public int Submit(AiImportDiscoverCommand command)
        {
            Commands.Add(command);
            Models[42] = new CommandModel { Id = 42, Body = command, Status = CommandStatus.Queued };
            return 42;
        }

        public CommandModel? Get(int commandId) => Models.GetValueOrDefault(commandId);
        public CommandModel? Find(string jobId) => Models.Values.SingleOrDefault(model =>
            model.Body is AiImportDiscoverCommand command && command.JobId == jobId);
    }

    private sealed class StubResolver : IAiImportSettingsResolver
    {
        public List<int> RequestedIds { get; } = [];
        public AiImportSettings ResolveEnabled(int requestedDefinitionId)
        {
            RequestedIds.Add(requestedDefinitionId);
            return new AiImportSettings { ApiBaseUrl = "https://example.test/v1", Model = "model" };
        }
    }
}
