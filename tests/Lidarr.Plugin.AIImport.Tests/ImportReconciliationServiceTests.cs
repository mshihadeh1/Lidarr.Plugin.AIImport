using System.Text.Json.Nodes;
using Lidarr.Plugin.AIImport.Approval;
using Lidarr.Plugin.AIImport.Authorization;
using Lidarr.Plugin.AIImport.Execution;
using Lidarr.Plugin.AIImport.Filesystem;
using NzbDrone.Core.Datastore;

namespace Lidarr.Plugin.AIImport.Tests;

[TestFixture]
public sealed class ImportReconciliationServiceTests
{
    [Test]
    public async Task Verifies_command_history_track_file_binding_and_native_result()
    {
        var plan = Plan();
        var started = DateTimeOffset.Parse("2026-10-27T08:00:00Z");
        var client = new FakeClient
        {
            Command = new LidarrCommandState(42, "ManualImport", "completed", "successful"),
            History = [History(started.AddSeconds(1))],
            TrackFiles = [new LidarrTrackFileEvidence(501, 5, 10, "/music/track.flac", 123,
                Quality(), 0)],
            Tracks = [new LidarrTrackBinding(1001, 5, 10, 501, true)]
        };
        var verifier = new FakeVerifier(new VerifiedTransferResult(AuthId, TransferResultOutcome.Linked, 1, 2));
        var service = new ImportReconciliationService(client, verifier);

        var result = await service.ReconcileAsync(plan, [Binding()], 42, started, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(result.Status, Is.EqualTo(ImportReconciliationStatus.Succeeded));
            Assert.That(result.VerifiedFiles, Is.EqualTo(1));
            Assert.That(verifier.Destinations, Is.EqualTo(new[] { "/music/track.flac" }));
        });
    }

    [Test]
    public async Task Succeeds_when_all_files_verify_even_if_command_reports_failure()
    {
        var started = DateTimeOffset.Parse("2026-10-27T08:00:00Z");
        var client = new FakeClient
        {
            Command = new LidarrCommandState(42, "ManualImport", "failed", "unsuccessful"),
            History = [History(started.AddSeconds(1))],
            TrackFiles = [new LidarrTrackFileEvidence(501, 5, 10, "/music/track.flac", 123, Quality(), 0)],
            Tracks = [new LidarrTrackBinding(1001, 5, 10, 501, true)]
        };
        var service = new ImportReconciliationService(
            client, new FakeVerifier(new VerifiedTransferResult(AuthId, TransferResultOutcome.Linked, 1, 2)));

        var result = await service.ReconcileAsync(Plan(), [Binding()], 42, started, CancellationToken.None);

        Assert.That(result.Status, Is.EqualTo(ImportReconciliationStatus.Succeeded));
    }

    [Test]
    public async Task Recovers_lost_command_receipt_from_complete_per_file_evidence()
    {
        var started = DateTimeOffset.Parse("2026-10-27T08:00:00Z");
        var client = new FakeClient
        {
            Command = new LidarrCommandState(42, "ManualImport", "completed", "successful"),
            History = [History(started.AddSeconds(1))],
            TrackFiles = [new LidarrTrackFileEvidence(501, 5, 10, "/music/track.flac", 123, Quality(), 0)],
            Tracks = [new LidarrTrackBinding(1001, 5, 10, 501, true)]
        };
        var service = new ImportReconciliationService(
            client, new FakeVerifier(new VerifiedTransferResult(AuthId, TransferResultOutcome.Linked, 1, 2)));

        var result = await service.ReconcileAsync(Plan(), [Binding()], null, started, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(result.Status, Is.EqualTo(ImportReconciliationStatus.Succeeded));
            Assert.That(client.CommandReads, Is.Zero);
            Assert.That(result.Message, Does.Contain("receipt was unavailable"));
        });
    }

    [Test]
    public async Task Recovers_from_complete_per_file_evidence_when_lidarr_purged_the_command()
    {
        var started = DateTimeOffset.Parse("2026-10-27T08:00:00Z");
        var client = new FakeClient
        {
            Command = new LidarrCommandState(42, "ManualImport", "completed", "successful"),
            CommandReadException = new ModelNotFoundException(typeof(LidarrCommandState), 42),
            History = [History(started.AddSeconds(1))],
            TrackFiles = [new LidarrTrackFileEvidence(501, 5, 10, "/music/track.flac", 123, Quality(), 0)],
            Tracks = [new LidarrTrackBinding(1001, 5, 10, 501, true)]
        };
        var service = new ImportReconciliationService(
            client, new FakeVerifier(new VerifiedTransferResult(AuthId, TransferResultOutcome.Linked, 1, 2)));

        var result = await service.ReconcileAsync(Plan(), [Binding()], 42, started, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(result.Status, Is.EqualTo(ImportReconciliationStatus.Succeeded));
            Assert.That(client.CommandReads, Is.EqualTo(1));
            Assert.That(result.Message, Does.Contain("command record was purged"));
        });
    }

    [Test]
    public async Task Recovers_from_unique_album_history_when_download_id_and_source_path_were_rewritten()
    {
        var started = DateTimeOffset.Parse("2026-10-27T08:00:00Z");
        var rewritten = History(started.AddSeconds(1)) with
        {
            DownloadId = string.Empty,
            DroppedPath = "/rewritten/client/path/track.flac"
        };
        var client = new FakeClient
        {
            Command = new LidarrCommandState(42, "ManualImport", "completed", "successful"),
            History = [rewritten],
            TrackFiles = [new LidarrTrackFileEvidence(501, 5, 10, "/music/track.flac", 123, Quality(), 0)],
            Tracks = [new LidarrTrackBinding(1001, 5, 10, 501, true)]
        };
        var verifier = new FakeVerifier(new VerifiedTransferResult(AuthId, TransferResultOutcome.Linked, 1, 2));
        var service = new ImportReconciliationService(client, verifier);

        var result = await service.ReconcileAsync(Plan(), [Binding()], 42, started, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(result.Status, Is.EqualTo(ImportReconciliationStatus.Succeeded));
            Assert.That(result.Message, Does.Contain("rewrote"));
            Assert.That(verifier.Destinations, Is.EqualTo(new[] { "/music/track.flac" }));
        });
    }

    [Test]
    public async Task Recovers_from_unique_album_history_using_authoritative_current_metadata()
    {
        var started = DateTimeOffset.Parse("2026-10-27T08:00:00Z");
        var historical = History(started.AddSeconds(1)) with
        {
            DownloadId = string.Empty,
            DroppedPath = "/rewritten/client/path/track.flac",
            Size = 999,
            Quality = new JsonObject { ["historical"] = true },
            IndexerFlags = 7
        };
        var client = new FakeClient
        {
            Command = new LidarrCommandState(42, "ManualImport", "completed", "successful"),
            CommandReadException = new ModelNotFoundException(typeof(LidarrCommandState), 42),
            History = [historical],
            TrackFiles = [new LidarrTrackFileEvidence(501, 5, 10, "/music/track.flac", 123, Quality(), 0)],
            Tracks = [new LidarrTrackBinding(1001, 5, 10, 501, true)]
        };
        var verifier = new FakeVerifier(new VerifiedTransferResult(AuthId, TransferResultOutcome.Linked, 1, 2));

        var result = await new ImportReconciliationService(client, verifier)
            .ReconcileAsync(Plan(), [Binding()], 42, started, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(result.Status, Is.EqualTo(ImportReconciliationStatus.Succeeded));
            Assert.That(result.Files!.Single().Size, Is.EqualTo(123));
            Assert.That(result.Files!.Single().IndexerFlags, Is.Zero);
            Assert.That(JsonNode.DeepEquals(result.Files!.Single().Quality, Quality()), Is.True);
        });
    }

    [Test]
    public void Rejects_ambiguous_album_history_fallback()
    {
        var started = DateTimeOffset.Parse("2026-10-27T08:00:00Z");
        var first = History(started.AddSeconds(1)) with { DownloadId = string.Empty, DroppedPath = "/rewritten/a.flac" };
        var second = first with { HistoryId = 602, TrackFileId = 502, ImportedPath = "/music/other.flac" };
        var client = new FakeClient
        {
            Command = new LidarrCommandState(42, "ManualImport", "completed", "successful"),
            History = [first, second]
        };
        var service = new ImportReconciliationService(
            client, new FakeVerifier(new VerifiedTransferResult(AuthId, TransferResultOutcome.Linked, 1, 2)));

        Assert.ThrowsAsync<ImportReconciliationException>(() => service.ReconcileAsync(
            Plan(), [Binding()], 42, started, CancellationToken.None));
    }

    [Test]
    public async Task Returns_pending_without_inspecting_files_while_command_is_running()
    {
        var client = new FakeClient { Command = new LidarrCommandState(42, "ManualImport", "started", "unknown") };
        var verifier = new FakeVerifier(new VerifiedTransferResult(AuthId, TransferResultOutcome.Linked, 1, 2));
        var service = new ImportReconciliationService(client, verifier);

        var result = await service.ReconcileAsync(
            Plan(), [Binding()], 42, DateTimeOffset.Parse("2026-10-27T08:00:00Z"), CancellationToken.None);

        Assert.That(result.Status, Is.EqualTo(ImportReconciliationStatus.Pending));
        Assert.That(verifier.Destinations, Is.Empty);
    }

    [Test]
    public async Task Returns_pending_when_terminal_command_history_is_not_visible_yet()
    {
        var client = new FakeClient
        {
            Command = new LidarrCommandState(42, "ManualImport", "completed", "successful"),
            History = []
        };
        var verifier = new FakeVerifier(new VerifiedTransferResult(AuthId, TransferResultOutcome.Linked, 1, 2));
        var service = new ImportReconciliationService(client, verifier);

        var result = await service.ReconcileAsync(
            Plan(), [Binding()], 42, DateTimeOffset.UtcNow, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(result.Status, Is.EqualTo(ImportReconciliationStatus.Pending));
            Assert.That(result.Message, Does.Contain("history is still becoming visible"));
            Assert.That(verifier.Destinations, Is.Empty);
        });
    }

    [Test]
    public void Rejects_missing_terminal_command_history_after_grace_period()
    {
        var client = new FakeClient
        {
            Command = new LidarrCommandState(42, "ManualImport", "completed", "successful"),
            History = []
        };
        var service = new ImportReconciliationService(
            client, new FakeVerifier(new VerifiedTransferResult(AuthId, TransferResultOutcome.Linked, 1, 2)));

        Assert.ThrowsAsync<ImportReconciliationException>(() => service.ReconcileAsync(
            Plan(), [Binding()], 42, DateTimeOffset.UtcNow.Subtract(TimeSpan.FromMinutes(6)), CancellationToken.None));
    }

    [Test]
    public void Rejects_any_native_source_identity_mismatch()
    {
        var started = DateTimeOffset.Parse("2026-10-27T08:00:00Z");
        var client = new FakeClient
        {
            Command = new LidarrCommandState(42, "ManualImport", "completed", "successful"),
            History = [History(started.AddSeconds(1))],
            TrackFiles = [new LidarrTrackFileEvidence(501, 5, 10, "/music/track.flac", 123, Quality(), 0)],
            Tracks = [new LidarrTrackBinding(1001, 5, 10, 501, true)]
        };
        var verifier = new FakeVerifier(new VerifiedTransferResult(AuthId, TransferResultOutcome.Linked, 1, 999));
        var service = new ImportReconciliationService(client, verifier);

        Assert.ThrowsAsync<ImportReconciliationException>(() => service.ReconcileAsync(
            Plan(), [Binding()], 42, started, CancellationToken.None));
    }

    private const string AuthId = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    private static ApprovalAuthorizationBinding Binding() =>
        new(AuthId, "source", 1001, "/downloads/track.flac", 1, 2);

    private static ApprovedImportPlan Plan() => new(
        "analysis", "download", 5, 10, 101, "safety",
        [new ApprovedImportFile("source", 77, "/downloads/track.flac", 1001, Quality(), 0, "download", 123,
            new FileIdentity(1, 2, 123, 4, 5, 8, 9, 420, 6, 7, 1))]);

    private static LidarrImportHistoryEvidence History(DateTimeOffset date) => new(
        601, 5, 10, 1001, "download", date, 501, "/downloads/track.flac", "/music/track.flac",
        123, 0, Quality());

    private static JsonObject Quality() => new() { ["quality"] = new JsonObject { ["id"] = 1 } };

    private sealed class FakeClient : ILidarrReconciliationClient
    {
        public required LidarrCommandState Command { get; init; }
        public Exception? CommandReadException { get; init; }
        public IReadOnlyList<LidarrImportHistoryEvidence> History { get; init; } = [];
        public IReadOnlyList<LidarrTrackFileEvidence> TrackFiles { get; init; } = [];
        public IReadOnlyList<LidarrTrackBinding> Tracks { get; init; } = [];
        public int CommandReads { get; private set; }
        public Task<LidarrCommandState> GetCommandAsync(int commandId, CancellationToken cancellationToken)
        {
            CommandReads++;
            if (CommandReadException is not null) throw CommandReadException;
            return Task.FromResult(Command);
        }
        public Task<IReadOnlyList<LidarrImportHistoryEvidence>> GetImportHistoryAsync(string downloadId, int albumId, CancellationToken cancellationToken) => Task.FromResult(History);
        public Task<IReadOnlyList<LidarrTrackFileEvidence>> GetTrackFilesAsync(int albumId, IReadOnlyList<int> trackFileIds, CancellationToken cancellationToken) => Task.FromResult(TrackFiles);
        public Task<IReadOnlyList<LidarrTrackBinding>> GetTracksAsync(IReadOnlyList<int> trackIds, CancellationToken cancellationToken) => Task.FromResult(Tracks);
    }

    private sealed class FakeVerifier(VerifiedTransferResult result) : ITransferResultVerifier
    {
        public List<string> Destinations { get; } = [];
        public Task<VerifiedTransferResult> VerifyAsync(string authorizationId, string destinationPath, CancellationToken cancellationToken)
        {
            Destinations.Add(destinationPath);
            return Task.FromResult(result);
        }
    }
}
