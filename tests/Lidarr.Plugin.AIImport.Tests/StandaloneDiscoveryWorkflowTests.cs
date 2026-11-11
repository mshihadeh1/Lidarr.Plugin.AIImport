using Lidarr.Plugin.AIImport.Approval;
using Lidarr.Plugin.AIImport.Configuration;
using Lidarr.Plugin.AIImport.Discovery;
using Lidarr.Plugin.AIImport.Lidarr;
using Lidarr.Plugin.AIImport.Matching;
using Lidarr.Plugin.AIImport.Workflows;
using System.Text.Json.Nodes;

namespace Lidarr.Plugin.AIImport.Tests;

[TestFixture]
public sealed class StandaloneDiscoveryWorkflowTests
{
    [Test]
    public void Discovery_returns_only_failed_imports_with_verified_deterministic_proposals()
    {
        var gateway = new FakeGateway(
            [
                new QueueCandidate(3, 10, "ignored", TrackedImportState.Imported, "ignored"),
                new QueueCandidate(2, 10, "failed", TrackedImportState.ImportFailed, "failed"),
                new QueueCandidate(1, 10, "blocked", TrackedImportState.ImportBlocked, "blocked")
            ],
            new Dictionary<string, IReadOnlyList<SourceFileCandidate>>
            {
                ["failed"] = [Source("failed-source")],
                ["blocked"] = [Source("blocked-source")]
            },
            new Dictionary<int, IReadOnlyList<ReleaseCandidate>> { [10] = [Release()] });
        var workflow = new FailedImportDiscoveryWorkflow(gateway, new DeterministicMatcher(), null);

        var result = workflow.Discover(Settings());

        Assert.Multiple(() =>
        {
            Assert.That(result.CandidateCount, Is.EqualTo(2));
            Assert.That(result.Analyses.Select(x => x.DownloadId), Is.EqualTo(new[] { "blocked", "failed" }));
            Assert.That(result.Analyses, Has.All.Matches<AiImportAnalysisDto>(x =>
                x.Proposal.Status == ProposalStatus.Proposed &&
                x.Proposal.ArtistId == 5 && x.Proposal.AlbumId == 10 && x.Proposal.ReleaseId == 101));
            Assert.That(gateway.ManualImportDownloadIds, Is.EqualTo(new[] { "blocked", "failed" }));
            Assert.That(gateway.ReleaseAlbumIds, Is.EqualTo(new[] { 10, 10 }));
        });
    }

    [Test]
    public void Discovery_skips_a_vanished_stale_download_without_hiding_valid_candidates()
    {
        var gateway = new FakeGateway(
            [
                new QueueCandidate(1, 10, "stale", TrackedImportState.ImportBlocked, "stale"),
                new QueueCandidate(2, 10, "valid", TrackedImportState.ImportBlocked, "valid")
            ],
            new Dictionary<string, IReadOnlyList<SourceFileCandidate>> { ["valid"] = [Source("source-1")] },
            new Dictionary<int, IReadOnlyList<ReleaseCandidate>> { [10] = [Release()] },
            new HashSet<string>(StringComparer.Ordinal) { "stale" });

        var result = new FailedImportDiscoveryWorkflow(gateway, new DeterministicMatcher(), null).Discover(Settings());

        Assert.That(result.Analyses.Select(x => x.DownloadId), Is.EqualTo(new[] { "valid" }));
    }

    [Test]
    public void Discovery_skips_stale_failed_queue_entries_with_no_pending_download_files()
    {
        var gateway = new FakeGateway(
            [
                new QueueCandidate(1, 10, "already imported", TrackedImportState.ImportBlocked, "stale"),
                new QueueCandidate(2, 10, "pending", TrackedImportState.ImportFailed, "pending")
            ],
            new Dictionary<string, IReadOnlyList<SourceFileCandidate>>
            {
                ["stale"] = [],
                ["pending"] = [Source("source-1")]
            },
            new Dictionary<int, IReadOnlyList<ReleaseCandidate>> { [10] = [Release()] });

        var result = new FailedImportDiscoveryWorkflow(gateway, new DeterministicMatcher(), null).Discover(Settings());

        Assert.Multiple(() =>
        {
            Assert.That(result.Analyses.Select(x => x.DownloadId), Is.EqualTo(new[] { "pending" }));
            Assert.That(gateway.ManualImportDownloadIds, Is.EqualTo(new[] { "stale", "pending" }));
            Assert.That(gateway.ReleaseAlbumIds, Is.EqualTo(new[] { 10 }));
        });
    }

    [Test]
    public void Discovery_reports_verified_already_imported_files_without_making_them_actionable()
    {
        var gateway = new FakeGateway(
            [new QueueCandidate(1, 10, "stale", TrackedImportState.ImportFailed, "stale")],
            new Dictionary<string, IReadOnlyList<SourceFileCandidate>> { ["stale"] = [] },
            new Dictionary<int, IReadOnlyList<ReleaseCandidate>>(),
            scanCounts: new Dictionary<string, (int Imported, int Indeterminate)> { ["stale"] = (2, 0) },
            queueSnapshots:
            [
                [new QueueCandidate(1, 10, "stale", TrackedImportState.ImportFailed, "stale")],
                []
            ]);
        var settings = Settings(); settings.YoloMode = true;

        var result = new FailedImportDiscoveryWorkflow(gateway, new DeterministicMatcher(), null).Discover(settings);

        Assert.Multiple(() =>
        {
            Assert.That(result.Analyses, Is.Empty);
            Assert.That(result.Diagnostics.Single().AlreadyImportedCount, Is.EqualTo(2));
            Assert.That(result.Diagnostics.Single().IndeterminateCount, Is.Zero);
            Assert.That(gateway.RemovedActivityDownloadIds, Is.EqualTo(new[] { "stale" }));
        });
    }

    [Test]
    public void Yolo_success_discards_the_old_snapshot_and_rediscovers_before_returning()
    {
        var first = new QueueCandidate(1, 10, "first", TrackedImportState.ImportFailed, "first");
        var gateway = new FakeGateway(
            [first],
            new Dictionary<string, IReadOnlyList<SourceFileCandidate>> { ["first"] = [Source("source-1")] },
            new Dictionary<int, IReadOnlyList<ReleaseCandidate>> { [10] = [Release()] },
            queueSnapshots: new IReadOnlyList<QueueCandidate>[] { [first], [] });
        var coordinator = new FakeYoloCoordinator(new YoloImportResult("analysis", "complete", "verified"));
        using var temp = new TempDirectory();
        var settings = Settings(); settings.YoloMode = true;

        var result = new FailedImportDiscoveryWorkflow(
            gateway, new DeterministicMatcher(), null,
            new StandaloneAnalysisStore(Path.Combine(temp.Path, "analyses.json")),
            new FakeYoloAdder(), null!, coordinator).Discover(settings);

        Assert.Multiple(() =>
        {
            Assert.That(gateway.TrackedDownloadCalls, Is.EqualTo(2));
            Assert.That(coordinator.Calls, Is.EqualTo(1));
            Assert.That(result.Analyses, Is.Empty);
            Assert.That(result.YoloContinuation, Is.False);
            Assert.That(result.Diagnostics.Any(x => x.Message.Contains("deferred", StringComparison.OrdinalIgnoreCase)), Is.False);
        });
    }

    [Test]
    public void Yolo_pending_result_stops_without_mutating_a_second_album()
    {
        var queue = new[]
        {
            new QueueCandidate(1, 10, "first", TrackedImportState.ImportFailed, "first"),
            new QueueCandidate(2, 10, "second", TrackedImportState.ImportFailed, "second")
        };
        var gateway = new FakeGateway(
            queue,
            new Dictionary<string, IReadOnlyList<SourceFileCandidate>>
            {
                ["first"] = [Source("source-1")],
                ["second"] = [Source("source-2")]
            },
            new Dictionary<int, IReadOnlyList<ReleaseCandidate>> { [10] = [Release()] });
        var coordinator = new FakeYoloCoordinator(new YoloImportResult("analysis", "pending", "still running"));
        using var temp = new TempDirectory();
        var settings = Settings(); settings.YoloMode = true;

        var result = new FailedImportDiscoveryWorkflow(
            gateway, new DeterministicMatcher(), null,
            new StandaloneAnalysisStore(Path.Combine(temp.Path, "analyses.json")),
            new FakeYoloAdder(), null!, coordinator).Discover(settings);

        Assert.Multiple(() =>
        {
            Assert.That(gateway.TrackedDownloadCalls, Is.EqualTo(1));
            Assert.That(coordinator.Calls, Is.EqualTo(1));
            Assert.That(result.Analyses, Has.Count.EqualTo(2));
            Assert.That(result.Diagnostics.Single(x => x.Message.Contains("still running", StringComparison.Ordinal)).IndeterminateCount, Is.Zero);
        });
    }

    [Test]
    public void Discovery_excludes_indeterminate_import_evidence_and_reports_reconciliation_required()
    {
        var gateway = new FakeGateway(
            [new QueueCandidate(1, 10, "uncertain", TrackedImportState.ImportFailed, "uncertain")],
            new Dictionary<string, IReadOnlyList<SourceFileCandidate>> { ["uncertain"] = [] },
            new Dictionary<int, IReadOnlyList<ReleaseCandidate>>(),
            scanCounts: new Dictionary<string, (int Imported, int Indeterminate)> { ["uncertain"] = (0, 1) });

        var result = new FailedImportDiscoveryWorkflow(gateway, new DeterministicMatcher(), null).Discover(Settings());

        Assert.Multiple(() =>
        {
            Assert.That(result.Analyses, Is.Empty);
            Assert.That(result.Diagnostics.Single().IndeterminateCount, Is.EqualTo(1));
            Assert.That(result.Diagnostics.Single().Message, Does.Contain("reconciliation"));
        });
    }

    [Test]
    public void Discovery_output_contains_no_absolute_source_paths_or_provider_secrets()
    {
        var source = Source("source-1") with { SourcePath = "/downloads/Artist/Album/01 Song.flac" };
        var gateway = new FakeGateway(
            [new QueueCandidate(1, 10, "failed", TrackedImportState.ImportFailed, "failed")],
            new Dictionary<string, IReadOnlyList<SourceFileCandidate>> { ["failed"] = [source] },
            new Dictionary<int, IReadOnlyList<ReleaseCandidate>> { [10] = [Release()] });

        var result = new FailedImportDiscoveryWorkflow(gateway, new DeterministicMatcher(), null).Discover(Settings());
        var dto = result.Analyses.Single();

        Assert.Multiple(() =>
        {
            Assert.That(dto.Files.Single().Name, Is.EqualTo("01 - Song.flac"));
            Assert.That(dto.Candidates.Single().Tracks.Single(), Is.EqualTo(
                new ReleaseTrackDto(1001, "Song", 1, "1", 180000, false)));
            Assert.That(dto.GetType().GetProperties().Select(x => x.Name), Does.Not.Contain("ApiKey"));
            Assert.That(dto.Files.Single().GetType().GetProperties().Select(x => x.Name), Does.Not.Contain("SourcePath"));
        });
    }

    [Test]
    public void Ambiguous_candidates_use_llm_with_enabled_saved_settings_only()
    {
        var alternate = Release() with { Id = 102, ForeignReleaseId = "release-102" };
        var gateway = new FakeGateway(
            [new QueueCandidate(1, 10, "failed", TrackedImportState.ImportFailed, "failed")],
            new Dictionary<string, IReadOnlyList<SourceFileCandidate>> { ["failed"] = [Source("source-1")] },
            new Dictionary<int, IReadOnlyList<ReleaseCandidate>> { [10] = [Release(), alternate] });
        var llm = new StubLlm(new MatchProposal(
            1, ProposalStatus.Proposed, 5, 10, 101, .95m, "verified",
            [new TrackProposal("source-1", 1001, .96m)]));
        var settings = Settings();

        var result = new FailedImportDiscoveryWorkflow(gateway, new DeterministicMatcher(), llm).Discover(settings);

        Assert.Multiple(() =>
        {
            Assert.That(result.Analyses.Single().UsedLlm, Is.True);
            Assert.That(llm.Settings, Is.SameAs(settings));
            Assert.That(llm.RankedReleaseIds, Is.EqualTo(new[] { 101, 102 }));
        });
    }

    [Test]
    public void Discovery_persists_only_approval_material_under_the_standalone_analysis_id()
    {
        using var temp = new TempDirectory();
        var gateway = new FakeGateway(
            [new QueueCandidate(1, 10, "failed", TrackedImportState.ImportFailed, "failed")],
            new Dictionary<string, IReadOnlyList<SourceFileCandidate>> { ["failed"] = [Source("source-1")] },
            new Dictionary<int, IReadOnlyList<ReleaseCandidate>> { [10] = [Release()] });
        var store = new StandaloneAnalysisStore(Path.Combine(temp.Path, "analyses.json"));

        var analysis = new FailedImportDiscoveryWorkflow(gateway, new DeterministicMatcher(), null, store)
            .Discover(Settings()).Analyses.Single();
        var persisted = store.GetRequired(analysis.AnalysisId);

        Assert.Multiple(() =>
        {
            Assert.That(persisted.DownloadId, Is.EqualTo("failed"));
            Assert.That(persisted.Proposal with { Tracks = [] }, Is.EqualTo(analysis.Proposal with { Tracks = [] }));
            Assert.That(persisted.Proposal.Tracks, Is.EqualTo(analysis.Proposal.Tracks));
            Assert.That(File.ReadAllText(Path.Combine(temp.Path, "analyses.json")), Does.Not.Contain("secret"));
        });
    }

    [Test]
    public void Discovery_and_approval_use_the_queue_album_when_it_differs_from_the_selected_album()
    {
        var files = new[] { Source("source-1") };

        var queue = new[]
        {
            new QueueCandidate(2, 30, "duplicate", TrackedImportState.ImportFailed, "download"),
            new QueueCandidate(1, 20, "selected", TrackedImportState.ImportFailed, "download")
        };

        var discoveryAlbumIds = FailedImportDiscoveryWorkflow.GetCandidateAlbumIds(files, queue.OrderBy(x => x.Id).First().AlbumId);
        var approvalAlbumIds = StandaloneApprovedPlanProvider.GetCandidateAlbumIds(files, queue);

        Assert.Multiple(() =>
        {
            Assert.That(discoveryAlbumIds, Is.EqualTo(new[] { 10, 20 }));
            Assert.That(approvalAlbumIds, Is.EqualTo(discoveryAlbumIds));
        });
    }

    [Test]
    public void Discovery_scans_untracked_download_folders_for_albums_outside_the_queue()
    {
        using var temp = new TempDirectory();
        var rootPath = temp.Path;
        var albumDir = Path.Combine(rootPath, "Artist", "Album");
        Directory.CreateDirectory(albumDir);
        var filePath = Path.Combine(albumDir, "01 - Song.flac");
        File.WriteAllText(filePath, "");
        var files = new[]
        {
            Source("untracked-a") with
            {
                SourcePath = filePath,
                Quality = new JsonObject { ["quality"] = "Lossless" },
                Size = 12345,
                ManualImportId = 1,
                DownloadId = null
            }
        };
        var gateway = new FakeGateway(
            [new QueueCandidate(1, 99, "queue", TrackedImportState.ImportFailed, "queue")],
            new Dictionary<string, IReadOnlyList<SourceFileCandidate>> { ["queue"] = [] },
            new Dictionary<int, IReadOnlyList<ReleaseCandidate>> { [10] = [Release()] },
            untrackedScans: new Dictionary<string, ManualImportScanResult>
            {
                [albumDir] = new(files, 0, 0)
            });
        var settings = Settings();
        settings.ScanRoots = rootPath;

        var result = new FailedImportDiscoveryWorkflow(gateway, new DeterministicMatcher(), null).Discover(settings);
        var dto = result.Analyses.Single();

        Assert.Multiple(() =>
        {
            Assert.That(dto.FolderPath, Is.EqualTo(albumDir));
            Assert.That(dto.DownloadId, Is.EqualTo(string.Empty));
            Assert.That(dto.Proposal.Status, Is.EqualTo(ProposalStatus.Proposed));
            Assert.That(dto.Proposal.ArtistId, Is.EqualTo(5));
            Assert.That(dto.Proposal.AlbumId, Is.EqualTo(10));
            Assert.That(dto.Proposal.ReleaseId, Is.EqualTo(101));
            Assert.That(gateway.UntrackedScanFolders, Is.EqualTo(new[] { albumDir }));
        });
    }

    [Test]
    public void Discovery_scanning_an_empty_untracked_root_yields_no_analyses()
    {
        using var temp = new TempDirectory();
        var settings = Settings();
        settings.ScanRoots = temp.Path;

        var result = new FailedImportDiscoveryWorkflow(
            new FakeGateway(
                [],
                new Dictionary<string, IReadOnlyList<SourceFileCandidate>>(),
                new Dictionary<int, IReadOnlyList<ReleaseCandidate>>()),
            new DeterministicMatcher(), null).Discover(settings);

        Assert.That(result.Analyses, Is.Empty);
    }

    [Test]
    public void Yolo_mode_promotes_the_best_verified_mapping_without_an_llm()
    {
        var alternate = Release() with { Id = 102, ForeignReleaseId = "release-102" };
        var gateway = new FakeGateway(
            [new QueueCandidate(1, 10, "failed", TrackedImportState.ImportFailed, "failed")],
            new Dictionary<string, IReadOnlyList<SourceFileCandidate>> { ["failed"] = [Source("source-1")] },
            new Dictionary<int, IReadOnlyList<ReleaseCandidate>> { [10] = [Release(), alternate] });
        var settings = Settings();
        settings.YoloMode = true;

        var result = new FailedImportDiscoveryWorkflow(gateway, new DeterministicMatcher(), null).Discover(settings);
        var dto = result.Analyses.Single();

        Assert.Multiple(() =>
        {
            Assert.That(result.YoloMode, Is.True);
            Assert.That(dto.Proposal.Status, Is.EqualTo(ProposalStatus.Proposed));
            Assert.That(dto.Proposal.ReleaseId, Is.EqualTo(101));
            Assert.That(dto.Proposal.Explanation, Does.StartWith("YOLO mode:"));
            Assert.That(dto.Proposal.Tracks, Has.Exactly(1).Items.Matches<TrackProposal>(x => x.TrackId == 1001));
            Assert.That(dto.UsedLlm, Is.False);
        });
    }

    [Test]
    public void Yolo_mode_marks_an_already_deterministic_mapping_as_an_automated_override()
    {
        var gateway = new FakeGateway(
            [new QueueCandidate(1, 10, "failed", TrackedImportState.ImportFailed, "failed")],
            new Dictionary<string, IReadOnlyList<SourceFileCandidate>> { ["failed"] = [Source("source-1")] },
            new Dictionary<int, IReadOnlyList<ReleaseCandidate>> { [10] = [Release()] });
        var settings = Settings();
        settings.YoloMode = true;

        var analysis = new FailedImportDiscoveryWorkflow(gateway, new DeterministicMatcher(), null)
            .Discover(settings).Analyses.Single();

        Assert.Multiple(() =>
        {
            Assert.That(analysis.Proposal.Status, Is.EqualTo(ProposalStatus.Proposed));
            Assert.That(analysis.AutomatedOverride, Is.True);
        });
    }

    [Test]
    public void Ambiguous_candidates_stay_in_manual_review_when_yolo_mode_is_off()
    {
        var alternate = Release() with { Id = 102, ForeignReleaseId = "release-102" };
        var gateway = new FakeGateway(
            [new QueueCandidate(1, 10, "failed", TrackedImportState.ImportFailed, "failed")],
            new Dictionary<string, IReadOnlyList<SourceFileCandidate>> { ["failed"] = [Source("source-1")] },
            new Dictionary<int, IReadOnlyList<ReleaseCandidate>> { [10] = [Release(), alternate] });

        var result = new FailedImportDiscoveryWorkflow(gateway, new DeterministicMatcher(), null).Discover(Settings());

        Assert.Multiple(() =>
        {
            Assert.That(result.YoloMode, Is.False);
            Assert.That(result.Analyses.Single().Proposal.Status, Is.EqualTo(ProposalStatus.ManualReview));
        });
    }

    [Test]
    public void Yolo_mode_adds_missing_tagged_library_entities_and_reanalyzes()
    {
        using var temp = new TempDirectory();
        var albumDir = Path.Combine(temp.Path, "Artist", "Album");
        Directory.CreateDirectory(albumDir);
        var filePath = Path.Combine(albumDir, "01 - Song.flac");
        File.WriteAllText(filePath, "");
        var unlinked = Source("untracked-a") with
        {
            SourcePath = filePath,
            ArtistId = null,
            AlbumId = null,
            ArtistMusicBrainzId = "5df68e5e-c4b8-3f2d-9a1e-6c4a1b2c3d4e",
            AlbumMusicBrainzId = "1b2c3d4e-5df6-48e5-c4b8-3f2d9a1e6c4a",
            ReleaseMusicBrainzId = "6c4a1b2c-3d4e-5df6-8e5c-4b83f2d9a1e6"
        };
        var linked = unlinked with { ArtistId = 5, AlbumId = 10 };
        var adder = new FakeYoloAdder();
        var gateway = new FakeGateway(
            [],
            new Dictionary<string, IReadOnlyList<SourceFileCandidate>>(),
            new Dictionary<int, IReadOnlyList<ReleaseCandidate>>
            {
                [10] = [Release() with { ForeignReleaseId = unlinked.ReleaseMusicBrainzId! }]
            },
            untrackedScans: new Dictionary<string, ManualImportScanResult> { [albumDir] = new([unlinked], 0, 0) },
            untrackedRescans: new Dictionary<string, ManualImportScanResult> { [albumDir] = new([linked], 0, 0) });
        using var storeTemp = new TempDirectory();
        var settings = Settings();
        settings.ScanRoots = temp.Path;
        settings.YoloMode = true;

        var result = new FailedImportDiscoveryWorkflow(
            gateway, new DeterministicMatcher(), null,
            new StandaloneAnalysisStore(Path.Combine(storeTemp.Path, "analyses.json")), adder)
            .Discover(settings);
        var dto = result.Analyses.Single();

        Assert.Multiple(() =>
        {
            Assert.That(adder.Additions, Has.Count.EqualTo(1));
            Assert.That(adder.Additions[0].ArtistMusicBrainzId, Is.EqualTo(unlinked.ArtistMusicBrainzId));
            Assert.That(dto.Proposal.Status, Is.EqualTo(ProposalStatus.Proposed));
            Assert.That(dto.Proposal.ReleaseId, Is.EqualTo(101));
            Assert.That(dto.LibraryAddition, Is.Null);
            Assert.That(result.YoloMode, Is.True);
        });
    }

    [Test]
    public void Yolo_mode_keeps_the_manual_flow_when_the_automated_addition_fails()
    {
        using var temp = new TempDirectory();
        var albumDir = Path.Combine(temp.Path, "Artist", "Album");
        Directory.CreateDirectory(albumDir);
        File.WriteAllText(Path.Combine(albumDir, "01 - Song.flac"), "");
        var unlinked = Source("untracked-a") with
        {
            SourcePath = Path.Combine(albumDir, "01 - Song.flac"),
            ArtistId = null,
            AlbumId = null,
            ArtistMusicBrainzId = "5df68e5e-c4b8-3f2d-9a1e-6c4a1b2c3d4e",
            AlbumMusicBrainzId = "1b2c3d4e-5df6-48e5-c4b8-3f2d9a1e6c4a",
            ReleaseMusicBrainzId = "6c4a1b2c-3d4e-5df6-8e5c-4b83f2d9a1e6"
        };
        var adder = new FakeYoloAdder { Throw = new InvalidOperationException("MusicBrainz unreachable") };
        var gateway = new FakeGateway(
            [],
            new Dictionary<string, IReadOnlyList<SourceFileCandidate>>(),
            new Dictionary<int, IReadOnlyList<ReleaseCandidate>> { [10] = [Release()] },
            untrackedScans: new Dictionary<string, ManualImportScanResult> { [albumDir] = new([unlinked], 0, 0) });
        using var storeTemp = new TempDirectory();
        var settings = Settings();
        settings.ScanRoots = temp.Path;
        settings.YoloMode = true;
        var coordinator = new FakeYoloCoordinator(new YoloImportResult("analysis", "complete", "must not run"));

        var result = new FailedImportDiscoveryWorkflow(
            gateway, new DeterministicMatcher(), null,
            new StandaloneAnalysisStore(Path.Combine(storeTemp.Path, "analyses.json")), adder, null!, coordinator)
            .Discover(settings);
        var dto = result.Analyses.Single();

        Assert.Multiple(() =>
        {
            Assert.That(adder.Additions, Has.Count.EqualTo(1));
            Assert.That(dto.Proposal.Status, Is.EqualTo(ProposalStatus.ManualReview));
            Assert.That(dto.LibraryAddition, Is.Not.Null);
            Assert.That(result.Diagnostics.Single().Message, Does.Contain("YOLO mode: automatic library addition failed"));
            Assert.That(coordinator.Calls, Is.Zero);
        });
    }

    private static AiImportSettings Settings() => new()
    {
        ApiBaseUrl = "https://saved.example/v1",
        ApiKey = "secret",
        Model = "saved-model"
    };

    private static SourceFileCandidate Source(string id) =>
        new(id, "01 - Song.flac", "Artist", "Album", "Song", 1, [1], 180000, null, [], 5, 10);

    private static ReleaseCandidate Release() =>
        new(10, 101, "Artist", "Album", "release-101", "Album", string.Empty,
            [new TrackCandidate(1001, "Song", 1, "1", 180000, null, "recording-1", false)], 5);

    private sealed class FakeGateway(
        IReadOnlyList<QueueCandidate> queue,
        IReadOnlyDictionary<string, IReadOnlyList<SourceFileCandidate>> sources,
        IReadOnlyDictionary<int, IReadOnlyList<ReleaseCandidate>> releases,
        IReadOnlySet<string>? vanishedDownloads = null,
        IReadOnlyDictionary<string, (int Imported, int Indeterminate)>? scanCounts = null,
        IReadOnlyDictionary<string, ManualImportScanResult>? untrackedScans = null,
        IReadOnlyDictionary<string, ManualImportScanResult>? untrackedRescans = null,
        IReadOnlyList<IReadOnlyList<QueueCandidate>>? queueSnapshots = null) : IInProcessLidarrGateway
    {
        public List<string> ManualImportDownloadIds { get; } = [];
        public List<int> ReleaseAlbumIds { get; } = [];
        public List<string> UntrackedScanFolders { get; } = [];
        public List<string> RemovedActivityDownloadIds { get; } = [];
        public int TrackedDownloadCalls { get; private set; }
        private readonly Dictionary<string, int> _untrackedCalls = new();
        public string Version => FailedImportDiscoveryWorkflow.CompatibleLidarrVersion;
        public IReadOnlyList<QueueCandidate> GetTrackedDownloads()
        {
            var call = TrackedDownloadCalls++;
            return queueSnapshots is not null && call < queueSnapshots.Count ? queueSnapshots[call] : queue;
        }
        public IReadOnlyList<SourceFileCandidate> GetManualImports(string downloadId)
        {
            ManualImportDownloadIds.Add(downloadId);
            if (vanishedDownloads?.Contains(downloadId) == true)
                throw new FileNotFoundException("Tracked download vanished.");
            return sources[downloadId];
        }
        public ManualImportScanResult GetManualImportScan(string downloadId)
        {
            var files = GetManualImports(downloadId);
            return scanCounts is not null && scanCounts.TryGetValue(downloadId, out var counts)
                ? new ManualImportScanResult(files, counts.Imported, counts.Indeterminate)
                : new ManualImportScanResult(files, 0, 0);
        }
        public ManualImportScanResult GetUntrackedManualImportScan(string folderPath)
        {
            UntrackedScanFolders.Add(folderPath);
            var call = _untrackedCalls.TryGetValue(folderPath, out var count) ? count : 0;
            _untrackedCalls[folderPath] = call + 1;
            if (call > 0 && untrackedRescans is not null && untrackedRescans.TryGetValue(folderPath, out var rescan))
                return rescan;
            return untrackedScans is not null && untrackedScans.TryGetValue(folderPath, out var scan)
                ? scan
                : new ManualImportScanResult([], 0, 0);
        }
        public IReadOnlyList<ReleaseCandidate> GetReleaseCandidates(int albumId)
        {
            ReleaseAlbumIds.Add(albumId);
            return releases.TryGetValue(albumId, out var value) ? value : [];
        }
        public void RemoveVerifiedImportFromActivity(string downloadId) => RemovedActivityDownloadIds.Add(downloadId);
    }

    private sealed class FakeYoloCoordinator(YoloImportResult result) : IYoloImportCoordinator
    {
        public int Calls { get; private set; }
        public YoloImportResult? ProcessNext(IReadOnlyList<AiImportAnalysisDto> analyses)
        {
            Calls++;
            return result with { AnalysisId = analyses[0].AnalysisId };
        }
    }

    private sealed class FakeYoloAdder : IYoloLibraryAdder
    {
        public List<LibraryAdditionDto> Additions { get; } = [];
        public LibraryAdditionCreatedDto Result { get; set; } =
            new(5, 10, 101, "artist-mb", "album-mb", "release-mb", "complete", null);
        public Exception? Throw { get; set; }
        public LibraryAdditionCreatedDto TryAddTaggedAlbum(LibraryAdditionDto addition)
        {
            Additions.Add(addition);
            if (Throw is not null) throw Throw;
            return Result;
        }
    }

    private sealed class StubLlm(MatchProposal proposal) : ILlmMatchService
    {
        public AiImportSettings? Settings { get; private set; }
        public IReadOnlyList<int> RankedReleaseIds { get; private set; } = [];
        public MatchProposal Match(
            AiImportSettings settings,
            IReadOnlyList<SourceFileCandidate> files,
            IReadOnlyList<ReleaseCandidate> candidates,
            IReadOnlyList<int> rankedReleaseIds)
        {
            Settings = settings;
            RankedReleaseIds = rankedReleaseIds;
            return proposal;
        }
    }

    private sealed class TempDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ai-import-discovery-" + Guid.NewGuid().ToString("N"));
        public TempDirectory() => Directory.CreateDirectory(Path);
        public void Dispose() => Directory.Delete(Path, true);
    }
}
