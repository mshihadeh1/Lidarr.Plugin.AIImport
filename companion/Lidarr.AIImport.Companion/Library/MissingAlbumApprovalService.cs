using Lidarr.AIImport.Companion.Discovery;

namespace Lidarr.AIImport.Companion.Library;

public sealed class StaleAnalysisException : Exception
{
    public StaleAnalysisException(string message) : base(message)
    {
    }
}

public sealed class MissingAlbumApprovalService
{
    private readonly IFailedImportDiscoveryService _discovery;
    private readonly IMissingAlbumExecutor _missingAlbums;

    public MissingAlbumApprovalService(
        IFailedImportDiscoveryService discovery,
        IMissingAlbumExecutor missingAlbums)
    {
        _discovery = discovery;
        _missingAlbums = missingAlbums;
    }

    public async Task<EnsuredAlbum> ApproveAsync(
        string analysisId,
        int rootFolderId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(analysisId)) throw new ArgumentException("analysisId is required.");
        if (rootFolderId <= 0) throw new ArgumentOutOfRangeException(nameof(rootFolderId));
        var matches = (await _discovery.DiscoverAsync(cancellationToken).ConfigureAwait(false))
            .Where(x => string.Equals(x.AnalysisId, analysisId, StringComparison.Ordinal))
            .ToArray();
        if (matches.Length != 1)
            throw new StaleAnalysisException("The analysis is missing or stale; refresh discovery before approving album creation.");
        var proposal = matches[0].MissingAlbum
            ?? throw new StaleAnalysisException("The refreshed analysis no longer requires a missing album.");
        return await _missingAlbums.EnsureAsync(
            proposal.ArtistMusicBrainzId,
            proposal.ReleaseGroupMusicBrainzId,
            proposal.ReleaseMusicBrainzId,
            rootFolderId,
            cancellationToken).ConfigureAwait(false);
    }
}
