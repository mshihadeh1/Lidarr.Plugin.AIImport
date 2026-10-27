using Lidarr.AIImport.Companion.Discovery;
using Lidarr.AIImport.Companion.Lidarr;
using Lidarr.AIImport.Companion.Safety;

namespace Lidarr.AIImport.Companion.Tests;

[TestFixture]
public sealed class FailedImportDiscoveryServiceTests
{
    [Test]
    public async Task Discovers_only_failed_or_blocked_imports_and_returns_verified_proposal()
    {
        var client = new FakeLidarrClient(
            new[]
            {
                Queue(3, "imported", "ignored"),
                Queue(2, "importFailed", "failed"),
                Queue(1, "importBlocked", "blocked")
            },
            new Dictionary<string, IReadOnlyList<SourceFileCandidate>>
            {
                ["failed"] = new[] { Source("failed-file", albumId: 10) },
                ["blocked"] = new[] { Source("blocked-file", albumId: 10) }
            },
            new Dictionary<int, IReadOnlyList<ReleaseCandidate>>
            {
                [10] = new[] { Release() }
            });
        var service = new FailedImportDiscoveryService(client, new DeterministicMatcher());

        var results = await service.DiscoverAsync(CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(results.Select(x => x.DownloadId), Is.EqualTo(new[] { "blocked", "failed" }));
            Assert.That(results, Has.All.Matches<AnalysisResult>(x => x.Proposal.Status == ProposalStatus.Proposed));
            Assert.That(results, Has.All.Matches<AnalysisResult>(x => x.Proposal.ArtistId == 5 && x.Proposal.AlbumId == 10 && x.Proposal.ReleaseId == 101));
            Assert.That(client.ManualImportDownloadIds, Is.EqualTo(new[] { "blocked", "failed" }));
            Assert.That(client.ReleaseAlbumIds, Is.EqualTo(new[] { 10, 10 }));
        });
    }

    [Test]
    public async Task Uses_manual_import_album_when_queue_album_is_missing()
    {
        var client = new FakeLidarrClient(
            new[] { Queue(1, "importFailed", "failed", albumId: null) },
            new Dictionary<string, IReadOnlyList<SourceFileCandidate>>
            {
                ["failed"] = new[] { Source("f1", albumId: 10) }
            },
            new Dictionary<int, IReadOnlyList<ReleaseCandidate>> { [10] = new[] { Release() } });

        var result = (await new FailedImportDiscoveryService(client, new DeterministicMatcher())
            .DiscoverAsync(CancellationToken.None)).Single();

        Assert.That(result.Proposal.Status, Is.EqualTo(ProposalStatus.Proposed));
        Assert.That(client.ReleaseAlbumIds, Is.EqualTo(new[] { 10 }));
    }

    [Test]
    public async Task Missing_verified_album_candidate_requires_manual_review()
    {
        var client = new FakeLidarrClient(
            new[] { Queue(1, "importFailed", "failed", albumId: null) },
            new Dictionary<string, IReadOnlyList<SourceFileCandidate>>
            {
                ["failed"] = new[] { Source("f1", albumId: null) }
            },
            new Dictionary<int, IReadOnlyList<ReleaseCandidate>>());

        var result = (await new FailedImportDiscoveryService(client, new DeterministicMatcher())
            .DiscoverAsync(CancellationToken.None)).Single();

        Assert.Multiple(() =>
        {
            Assert.That(result.Proposal.Status, Is.EqualTo(ProposalStatus.ManualReview));
            Assert.That(result.Proposal.Tracks, Is.Empty);
            Assert.That(client.ReleaseAlbumIds, Is.Empty);
        });
    }

    [Test]
    public async Task Analysis_id_changes_when_verified_candidate_state_changes()
    {
        FakeLidarrClient Client(bool hasFile) => new(
            new[] { Queue(1, "importFailed", "failed") },
            new Dictionary<string, IReadOnlyList<SourceFileCandidate>>
            {
                ["failed"] = new[] { Source("f1", albumId: 10) }
            },
            new Dictionary<int, IReadOnlyList<ReleaseCandidate>> { [10] = new[] { Release(hasFile) } });

        var available = (await new FailedImportDiscoveryService(Client(false), new DeterministicMatcher())
            .DiscoverAsync(CancellationToken.None)).Single();
        var existing = (await new FailedImportDiscoveryService(Client(true), new DeterministicMatcher())
            .DiscoverAsync(CancellationToken.None)).Single();

        Assert.That(existing.AnalysisId, Is.Not.EqualTo(available.AnalysisId));
    }

    [Test]
    public void Rejects_unverified_lidarr_version_before_queue_discovery()
    {
        var client = new FakeLidarrClient(
            Array.Empty<QueueCandidate>(),
            new Dictionary<string, IReadOnlyList<SourceFileCandidate>>(),
            new Dictionary<int, IReadOnlyList<ReleaseCandidate>>())
        {
            SystemStatus = new LidarrSystemStatus("3.2.0.0", "3.2.0.0", "plugins")
        };

        Assert.ThrowsAsync<InvalidOperationException>(() =>
            new FailedImportDiscoveryService(client, new DeterministicMatcher()).DiscoverAsync(CancellationToken.None));
    }

    [Test]
    public async Task Duplicate_source_ids_cannot_produce_a_proposal()
    {
        var duplicate = Source("same-id", albumId: 10);
        var client = new FakeLidarrClient(
            new[] { Queue(1, "importFailed", "failed") },
            new Dictionary<string, IReadOnlyList<SourceFileCandidate>>
            {
                ["failed"] = new[] { duplicate, duplicate with { Name = "02 - Song.flac" } }
            },
            new Dictionary<int, IReadOnlyList<ReleaseCandidate>> { [10] = new[] { Release() } });

        var result = (await new FailedImportDiscoveryService(client, new DeterministicMatcher())
            .DiscoverAsync(CancellationToken.None)).Single();

        Assert.That(result.Proposal.Status, Is.EqualTo(ProposalStatus.ManualReview));
    }

    [Test]
    public async Task Invalid_deterministic_candidate_ids_are_downgraded_to_manual_review()
    {
        var invalidRelease = Release() with { Id = 0 };
        var client = new FakeLidarrClient(
            new[] { Queue(1, "importFailed", "failed") },
            new Dictionary<string, IReadOnlyList<SourceFileCandidate>>
            {
                ["failed"] = new[] { Source("f1", albumId: 10) }
            },
            new Dictionary<int, IReadOnlyList<ReleaseCandidate>> { [10] = new[] { invalidRelease } });

        var result = (await new FailedImportDiscoveryService(client, new DeterministicMatcher())
            .DiscoverAsync(CancellationToken.None)).Single();

        Assert.Multiple(() =>
        {
            Assert.That(result.Proposal.Status, Is.EqualTo(ProposalStatus.ManualReview));
            Assert.That(result.Proposal.Tracks, Is.Empty);
        });
    }

    private static QueueCandidate Queue(int id, string state, string downloadId, int? albumId = 10) =>
        new(id, albumId, "Album", "completed", state, downloadId, "/downloads/album", Array.Empty<string>());

    private static SourceFileCandidate Source(string id, int? albumId) =>
        new(id, "01 - Song.flac", "Artist", "Album", "Song", 1, new[] { 1 }, 180_000, null,
            Array.Empty<string>(), ArtistId: albumId.HasValue ? 5 : null, AlbumId: albumId);

    private static ReleaseCandidate Release(bool hasFile = false) =>
        new(10, 101, "Artist", "Album", "release-101", "Album", string.Empty,
            new[] { new TrackCandidate(1001, "Song", 1, "1", 180_000, null, "recording-1", hasFile) }, 5);

    private sealed class FakeLidarrClient : ILidarrClient
    {
        private readonly IReadOnlyList<QueueCandidate> _queue;
        private readonly IReadOnlyDictionary<string, IReadOnlyList<SourceFileCandidate>> _files;
        private readonly IReadOnlyDictionary<int, IReadOnlyList<ReleaseCandidate>> _releases;

        public FakeLidarrClient(
            IReadOnlyList<QueueCandidate> queue,
            IReadOnlyDictionary<string, IReadOnlyList<SourceFileCandidate>> files,
            IReadOnlyDictionary<int, IReadOnlyList<ReleaseCandidate>> releases)
        {
            _queue = queue;
            _files = files;
            _releases = releases;
        }

        public List<string> ManualImportDownloadIds { get; } = new();
        public List<int> ReleaseAlbumIds { get; } = new();
        public LidarrSystemStatus SystemStatus { get; init; } = new("3.1.2.4913", "3.1.2.4913", "plugins");

        public Task<LidarrSystemStatus> GetSystemStatusAsync(CancellationToken cancellationToken) =>
            Task.FromResult(SystemStatus);

        public Task<IReadOnlyList<QueueCandidate>> GetQueueAsync(CancellationToken cancellationToken) =>
            Task.FromResult(_queue);

        public Task<IReadOnlyList<SourceFileCandidate>> GetManualImportsAsync(
            string? downloadId,
            string? folder,
            CancellationToken cancellationToken)
        {
            ManualImportDownloadIds.Add(downloadId!);
            return Task.FromResult(_files[downloadId!]);
        }

        public Task<IReadOnlyList<ReleaseCandidate>> GetReleaseCandidatesAsync(int albumId, CancellationToken cancellationToken)
        {
            ReleaseAlbumIds.Add(albumId);
            return Task.FromResult(_releases.TryGetValue(albumId, out var releases)
                ? releases
                : (IReadOnlyList<ReleaseCandidate>)Array.Empty<ReleaseCandidate>());
        }

        public Task<ImportSafetyConfiguration> GetImportSafetyConfigurationAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new ImportSafetyConfiguration(true, true, "/script", false, "none", false, "no", false, false));
    }
}
