using Lidarr.Plugin.AIImport.Commands;
using Lidarr.Plugin.AIImport.Configuration;
using Lidarr.Plugin.AIImport.Discovery;
using Lidarr.Plugin.AIImport.Matching;
using Lidarr.Plugin.AIImport.Workflows;
using NzbDrone.Core.Messaging.Commands;

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
