using Lidarr.AIImport.Companion.Discovery;
using Lidarr.AIImport.Companion.Library;

namespace Lidarr.AIImport.Companion.Tests;

[TestFixture]
public sealed class MissingAlbumApprovalServiceTests
{
    [Test]
    public async Task Revalidates_analysis_before_adding_album()
    {
        var proposal = new MissingAlbumProposal(
            Guid.Parse("11111111-1111-1111-1111-111111111111"),
            Guid.Parse("22222222-2222-2222-2222-222222222222"),
            Guid.Parse("33333333-3333-3333-3333-333333333333"),
            "Artist",
            "Album");
        var discovery = new FakeDiscovery(new AnalysisResult(
            "analysis-1", "download", "Album",
            new MatchProposal(1, ProposalStatus.ManualReview, null, null, null, 0m, "missing", []),
            [], [], false, DateTimeOffset.UtcNow, proposal));
        var ensure = new FakeMissingAlbumExecutor(new EnsuredAlbum(
            10, 5, proposal.ReleaseGroupMusicBrainzId, proposal.ReleaseMusicBrainzId, true));

        var result = await new MissingAlbumApprovalService(discovery, ensure)
            .ApproveAsync("analysis-1", 7, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(result.AlbumId, Is.EqualTo(10));
            Assert.That(ensure.ArtistId, Is.EqualTo(proposal.ArtistMusicBrainzId));
            Assert.That(ensure.ReleaseGroupId, Is.EqualTo(proposal.ReleaseGroupMusicBrainzId));
            Assert.That(ensure.RootFolderId, Is.EqualTo(7));
        });
    }

    [Test]
    public void Rejects_a_stale_analysis_without_writing()
    {
        var discovery = new FakeDiscovery();
        var ensure = new FakeMissingAlbumExecutor(null);

        Assert.ThrowsAsync<StaleAnalysisException>(() =>
            new MissingAlbumApprovalService(discovery, ensure)
                .ApproveAsync("stale", 7, CancellationToken.None));
        Assert.That(ensure.Calls, Is.Zero);
    }

    private sealed class FakeDiscovery(params AnalysisResult[] results) : IFailedImportDiscoveryService
    {
        public Task<IReadOnlyList<AnalysisResult>> DiscoverAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<AnalysisResult>>(results);
    }

    private sealed class FakeMissingAlbumExecutor(EnsuredAlbum? result) : IMissingAlbumExecutor
    {
        public int Calls { get; private set; }
        public Guid ArtistId { get; private set; }
        public Guid ReleaseGroupId { get; private set; }
        public int RootFolderId { get; private set; }

        public Task<EnsuredAlbum> EnsureAsync(Guid artistId, Guid releaseGroupId, Guid releaseId, int rootFolderId, CancellationToken cancellationToken)
        {
            Calls++;
            ArtistId = artistId;
            ReleaseGroupId = releaseGroupId;
            RootFolderId = rootFolderId;
            return Task.FromResult(result ?? throw new AssertionException("Unexpected ensure call"));
        }
    }
}
