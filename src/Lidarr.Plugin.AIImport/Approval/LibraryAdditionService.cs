using Lidarr.Plugin.AIImport.Discovery;
using Lidarr.Plugin.AIImport.Lidarr;
using Lidarr.Plugin.AIImport.Workflows;

namespace Lidarr.Plugin.AIImport.Approval;

public sealed record LibraryAdditionRequest(
    string ArtistMusicBrainzId,
    string AlbumMusicBrainzId,
    string ReleaseMusicBrainzId,
    int RootFolderId,
    int QualityProfileId,
    int MetadataProfileId);

public sealed record LibraryAdditionCreatedDto(
    int ArtistId, int AlbumId, int ReleaseId,
    string ArtistMusicBrainzId, string AlbumMusicBrainzId, string ReleaseMusicBrainzId,
    string State = "complete", string? Outcome = null);

public interface ILibraryAdditionClient
{
    LibraryAdditionCreatedDto EnsureAlbum(LibraryAdditionRequest request);
}

public sealed class LibraryAdditionService
{
    private readonly StandaloneAnalysisStore _analyses;
    private readonly IInProcessLidarrGateway _gateway;
    private readonly ILibraryAdditionClient _client;

    public LibraryAdditionService(
        StandaloneAnalysisStore analyses,
        IInProcessLidarrGateway gateway,
        ILibraryAdditionClient client)
    {
        _analyses = analyses;
        _gateway = gateway;
        _client = client;
    }

    public LibraryAdditionCreatedDto Add(
        string analysisId,
        int rootFolderId,
        int qualityProfileId,
        int metadataProfileId,
        string confirmation)
    {
        if (!string.Equals(confirmation, "ADD", StringComparison.Ordinal))
            throw new ArgumentException("Exact library-addition confirmation ADD is required.", nameof(confirmation));
        if (rootFolderId <= 0) throw new ArgumentOutOfRangeException(nameof(rootFolderId));
        if (qualityProfileId <= 0) throw new ArgumentOutOfRangeException(nameof(qualityProfileId));
        if (metadataProfileId <= 0) throw new ArgumentOutOfRangeException(nameof(metadataProfileId));

        var stored = _analyses.GetRequired(analysisId);
        var queueMatches = _gateway.GetTrackedDownloads().Where(x =>
            string.Equals(x.DownloadId, stored.DownloadId, StringComparison.Ordinal) &&
            x.State is TrackedImportState.ImportBlocked or TrackedImportState.ImportFailed).ToArray();
        if (queueMatches.Length == 0)
            throw new StaleApprovalException("The failed import is no longer present in Lidarr.");
        var queueAlbumId = queueMatches.OrderBy(x => x.Id).First().AlbumId;
        var files = _gateway.GetManualImports(stored.DownloadId);
        var albumIds = FailedImportDiscoveryWorkflow.GetCandidateAlbumIds(files, queueAlbumId);
        var candidates = albumIds.SelectMany(_gateway.GetReleaseCandidates)
            .GroupBy(x => x.Id).Select(x => x.First()).OrderBy(x => x.Id).ToArray();
        var currentId = FailedImportDiscoveryWorkflow.ComputeAnalysisId(
            stored.DownloadId, queueAlbumId, files, candidates, stored.Proposal);
        if (!string.Equals(currentId, analysisId, StringComparison.Ordinal))
            throw new StaleApprovalException("The analysis no longer matches current Lidarr or source state.");
        if (candidates.Length != 0)
            throw new InvalidOperationException("Verified Lidarr candidates already exist; use mapping review instead of adding library entities.");
        var addition = FailedImportDiscoveryWorkflow.GetLibraryAddition(files)
            ?? throw new InvalidOperationException("Every source file must carry the same valid MusicBrainz artist, release-group, and release identifiers.");

        return _client.EnsureAlbum(new LibraryAdditionRequest(
            addition.ArtistMusicBrainzId,
            addition.AlbumMusicBrainzId,
            addition.ReleaseMusicBrainzId,
            rootFolderId,
            qualityProfileId,
            metadataProfileId));
    }
}
