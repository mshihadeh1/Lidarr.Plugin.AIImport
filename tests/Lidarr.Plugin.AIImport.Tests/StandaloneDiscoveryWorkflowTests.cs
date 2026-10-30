using Lidarr.Plugin.AIImport.Configuration;
using Lidarr.Plugin.AIImport.Discovery;
using Lidarr.Plugin.AIImport.Lidarr;
using Lidarr.Plugin.AIImport.Matching;
using Lidarr.Plugin.AIImport.Workflows;

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
        IReadOnlySet<string>? vanishedDownloads = null) : IInProcessLidarrGateway
    {
        public List<string> ManualImportDownloadIds { get; } = [];
        public List<int> ReleaseAlbumIds { get; } = [];
        public string Version => FailedImportDiscoveryWorkflow.CompatibleLidarrVersion;
        public IReadOnlyList<QueueCandidate> GetTrackedDownloads() => queue;
        public IReadOnlyList<SourceFileCandidate> GetManualImports(string downloadId)
        {
            ManualImportDownloadIds.Add(downloadId);
            if (vanishedDownloads?.Contains(downloadId) == true)
                throw new FileNotFoundException("Tracked download vanished.");
            return sources[downloadId];
        }
        public IReadOnlyList<ReleaseCandidate> GetReleaseCandidates(int albumId)
        {
            ReleaseAlbumIds.Add(albumId);
            return releases.TryGetValue(albumId, out var value) ? value : [];
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
