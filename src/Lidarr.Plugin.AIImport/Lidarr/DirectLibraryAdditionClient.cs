using Lidarr.Plugin.AIImport.Approval;
using NzbDrone.Core.MetadataSource;
using NzbDrone.Core.Music;
using NzbDrone.Core.Profiles.Metadata;
using NzbDrone.Core.Profiles.Qualities;
using NzbDrone.Core.RootFolders;

namespace Lidarr.Plugin.AIImport.Lidarr;

public sealed class DirectLibraryAdditionClient : ILibraryAdditionClient
{
    private static readonly object[] ArtistGates = Enumerable.Range(0, 256).Select(_ => new object()).ToArray();
    private readonly IArtistService _artists;
    private readonly IAlbumService _albums;
    private readonly IAddAlbumService _addAlbums;
    private readonly IProvideAlbumInfo _albumInfo;
    private readonly IRefreshAlbumService _refreshAlbums;
    private readonly IReleaseService _releases;
    private readonly ITrackService _tracks;
    private readonly IRootFolderService _rootFolders;
    private readonly IQualityProfileService _qualityProfiles;
    private readonly IMetadataProfileService _metadataProfiles;

    public DirectLibraryAdditionClient(
        IArtistService artists,
        IAlbumService albums,
        IAddAlbumService addAlbums,
        IProvideAlbumInfo albumInfo,
        IRefreshAlbumService refreshAlbums,
        IReleaseService releases,
        ITrackService tracks,
        IRootFolderService rootFolders,
        IQualityProfileService qualityProfiles,
        IMetadataProfileService metadataProfiles)
    {
        _artists = artists;
        _albums = albums;
        _addAlbums = addAlbums;
        _albumInfo = albumInfo;
        _refreshAlbums = refreshAlbums;
        _releases = releases;
        _tracks = tracks;
        _rootFolders = rootFolders;
        _qualityProfiles = qualityProfiles;
        _metadataProfiles = metadataProfiles;
    }

    public LibraryAdditionCreatedDto EnsureAlbum(LibraryAdditionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var artistGuid = ParseMusicBrainzId(request.ArtistMusicBrainzId, "artist");
        var albumGuid = ParseMusicBrainzId(request.AlbumMusicBrainzId, "release-group");
        var releaseGuid = ParseMusicBrainzId(request.ReleaseMusicBrainzId, "release");
        if (request.RootFolderId <= 0 || request.QualityProfileId <= 0 || request.MetadataProfileId <= 0)
            throw new ArgumentOutOfRangeException(nameof(request), "Positive Lidarr root-folder and profile IDs are required.");

        lock (ArtistGates[(artistGuid.GetHashCode() & int.MaxValue) % ArtistGates.Length])
        {
            var artistMusicBrainzId = artistGuid.ToString();
            var albumMusicBrainzId = albumGuid.ToString();
            var releaseMusicBrainzId = releaseGuid.ToString();

            // Complete every possible validation before the first Lidarr mutation.
            var root = _rootFolders.Get(request.RootFolderId, false)
                ?? throw new InvalidOperationException("The selected Lidarr root folder no longer exists.");
            if (!root.Accessible || string.IsNullOrWhiteSpace(root.Path) || !Path.IsPathFullyQualified(root.Path))
                throw new InvalidOperationException("The selected Lidarr root folder is not accessible.");
            if (!_qualityProfiles.Exists(request.QualityProfileId))
                throw new InvalidOperationException("The selected Lidarr quality profile no longer exists.");
            if (!_metadataProfiles.Exists(request.MetadataProfileId))
                throw new InvalidOperationException("The selected Lidarr metadata profile no longer exists.");

            var expectedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root.Path));
            var metadata = _albumInfo.GetAlbumInfo(albumMusicBrainzId);
            var remoteAlbum = metadata.Item2;
            if (!string.Equals(remoteAlbum.ForeignAlbumId, albumMusicBrainzId, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(metadata.Item1, artistMusicBrainzId, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("MusicBrainz returned a different album or owning artist.");
            var taggedRemoteReleases = remoteAlbum.AlbumReleases.Value.Where(x =>
                string.Equals(x.ForeignReleaseId, releaseMusicBrainzId, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (taggedRemoteReleases.Length != 1)
                throw new InvalidDataException("MusicBrainz did not return exactly one tagged release for the album.");

            var existingArtist = _artists.FindById(artistMusicBrainzId);
            if (existingArtist is not null)
                ValidateArtistSettings(existingArtist, expectedRoot, request);
            var existingAlbum = _albums.FindById(albumMusicBrainzId);
            if (existingAlbum is not null)
                ValidateAlbumOwner(existingAlbum, artistMusicBrainzId);

            var mutationStarted = false;
            try
            {
                var album = existingAlbum;
                if (album is null)
                {
                    var artist = existingArtist ?? new Artist
                    {
                        ForeignArtistId = artistMusicBrainzId,
                        RootFolderPath = expectedRoot,
                        QualityProfileId = request.QualityProfileId,
                        MetadataProfileId = request.MetadataProfileId,
                        Monitored = false,
                        MonitorNewItems = NewItemMonitorTypes.None,
                        AddOptions = new AddArtistOptions
                        {
                            Monitor = MonitorTypes.None,
                            SearchForMissingAlbums = false
                        }
                    };
                    mutationStarted = true;
                    var requestedAlbum = new Album
                    {
                        ForeignAlbumId = albumMusicBrainzId,
                        Artist = artist,
                        Monitored = true,
                        AnyReleaseOk = false,
                        AddOptions = new AddAlbumOptions { SearchForNewAlbum = false }
                    };
                    try
                    {
                        album = _addAlbums.AddAlbum(requestedAlbum, false);
                    }
                    catch
                    {
                        album = _albums.FindById(albumMusicBrainzId);
                        if (album is null) throw;
                    }
                }

                ValidateAlbumOwner(album, artistMusicBrainzId);
                var albumArtist = _artists.GetArtist(album.ArtistId);
                ValidateArtistSettings(albumArtist, expectedRoot, request);
                if (!album.Monitored || album.AnyReleaseOk)
                {
                    mutationStarted = true;
                    album.Monitored = true;
                    album.AnyReleaseOk = false;
                    album.AddOptions = new AddAlbumOptions { SearchForNewAlbum = false };
                    album = _albums.UpdateAlbum(album);
                }

                mutationStarted = true;
                _refreshAlbums.RefreshAlbumInfo(album, [remoteAlbum], false);
                album = _albums.FindById(albumMusicBrainzId)
                    ?? throw new InvalidOperationException("Lidarr did not persist the requested MusicBrainz album.");
                ValidateAlbumOwner(album, artistMusicBrainzId);
                var selected = _releases.GetReleasesByAlbum(album.Id).Where(x =>
                    string.Equals(x.ForeignReleaseId, releaseMusicBrainzId, StringComparison.OrdinalIgnoreCase)).ToArray();
                if (selected.Length != 1)
                    throw new InvalidDataException("Lidarr did not persist exactly one tagged MusicBrainz release.");
                _releases.SetMonitored(selected[0]);
                return VerifyComplete(albumMusicBrainzId, artistMusicBrainzId, releaseMusicBrainzId, expectedRoot, request);
            }
            catch (Exception) when (mutationStarted)
            {
                // Lidarr service writes are not transactional. Never claim that nothing changed:
                // return a durable, idempotently resumable partial result after canonical read-back.
                var artist = _artists.FindById(artistMusicBrainzId);
                var album = _albums.FindById(albumMusicBrainzId);
                return new LibraryAdditionCreatedDto(
                    artist?.Id ?? 0, album?.Id ?? 0, 0,
                    artistMusicBrainzId, albumMusicBrainzId, releaseMusicBrainzId,
                    "partial", "Lidarr may have persisted part of the exact MusicBrainz addition. Run Discover, then retry Add only if it is still offered.");
            }
        }
    }

    private LibraryAdditionCreatedDto VerifyComplete(
        string albumMusicBrainzId, string artistMusicBrainzId, string releaseMusicBrainzId,
        string expectedRoot, LibraryAdditionRequest request)
    {
        var album = _albums.FindById(albumMusicBrainzId)
            ?? throw new InvalidOperationException("Lidarr did not persist the requested MusicBrainz album.");
        ValidateAlbumOwner(album, artistMusicBrainzId);
        var artist = _artists.GetArtist(album.ArtistId);
        ValidateArtistSettings(artist, expectedRoot, request);
        var monitored = _releases.GetReleasesByAlbum(album.Id).Where(x => x.Monitored).ToArray();
        if (monitored.Length != 1 ||
            !string.Equals(monitored[0].ForeignReleaseId, releaseMusicBrainzId, StringComparison.OrdinalIgnoreCase) ||
            _tracks.GetTracksByRelease(monitored[0].Id).Count == 0)
            throw new InvalidDataException("Lidarr did not reconcile the exact tagged release and its tracks.");
        return new LibraryAdditionCreatedDto(
            artist.Id, album.Id, monitored[0].Id,
            artistMusicBrainzId, albumMusicBrainzId, releaseMusicBrainzId,
            "complete", "The exact MusicBrainz artist, album, release, and tracks were verified in Lidarr.");
    }

    private void ValidateAlbumOwner(Album album, string artistMusicBrainzId)
    {
        var artist = _artists.GetArtist(album.ArtistId);
        if (!string.Equals(artist.ForeignArtistId, artistMusicBrainzId, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The Lidarr album belongs to a different MusicBrainz artist.");
    }

    private static void ValidateArtistSettings(Artist artist, string expectedRoot, LibraryAdditionRequest request)
    {
        var actualRoot = string.IsNullOrWhiteSpace(artist.RootFolderPath)
            ? string.Empty
            : Path.TrimEndingDirectorySeparator(Path.GetFullPath(artist.RootFolderPath));
        if (!string.Equals(actualRoot, expectedRoot, StringComparison.Ordinal) ||
            artist.QualityProfileId != request.QualityProfileId ||
            artist.MetadataProfileId != request.MetadataProfileId)
            throw new InvalidOperationException("The existing Lidarr artist uses different root-folder or profile settings. Select its existing settings and try again.");
    }

    private static Guid ParseMusicBrainzId(string value, string kind)
    {
        if (!Guid.TryParseExact(value, "D", out var parsed) || parsed == Guid.Empty)
            throw new InvalidDataException($"The MusicBrainz {kind} identifier must be a canonical non-zero UUID.");
        return parsed;
    }
}
