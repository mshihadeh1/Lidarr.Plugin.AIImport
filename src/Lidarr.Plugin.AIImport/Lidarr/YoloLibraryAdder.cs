using Lidarr.Plugin.AIImport.Approval;
using Lidarr.Plugin.AIImport.Configuration;
using Lidarr.Plugin.AIImport.Discovery;
using NzbDrone.Core.Music;
using NzbDrone.Core.Profiles.Metadata;
using NzbDrone.Core.Profiles.Qualities;
using NzbDrone.Core.RootFolders;

namespace Lidarr.Plugin.AIImport.Lidarr;

/// <summary>
/// Adds tagged artists/albums to the library automatically in YOLO mode.
/// Destination choice: the artist's existing root folder and profiles when the
/// artist is already in the library (EnsureAlbum validates against those
/// settings), otherwise the first accessible root folder and the default
/// quality/metadata profiles.
/// </summary>
public interface IYoloLibraryAdder
{
    LibraryAdditionCreatedDto TryAddTaggedAlbum(LibraryAdditionDto addition);
    LibraryAdditionCreatedDto TryAddTaggedAlbum(LibraryAdditionDto addition, AiImportSettings settings) =>
        TryAddTaggedAlbum(addition);
}

public sealed class YoloLibraryAdder : IYoloLibraryAdder
{
    private readonly IArtistService _artists;
    private readonly IRootFolderService _rootFolders;
    private readonly IQualityProfileService _qualityProfiles;
    private readonly IMetadataProfileService _metadataProfiles;
    private readonly ILibraryAdditionClient _client;

    public YoloLibraryAdder(
        IArtistService artists,
        IRootFolderService rootFolders,
        IQualityProfileService qualityProfiles,
        IMetadataProfileService metadataProfiles,
        ILibraryAdditionClient client)
    {
        _artists = artists;
        _rootFolders = rootFolders;
        _qualityProfiles = qualityProfiles;
        _metadataProfiles = metadataProfiles;
        _client = client;
    }

    public LibraryAdditionCreatedDto TryAddTaggedAlbum(LibraryAdditionDto addition)
    {
        var (rootFolderId, qualityProfileId, metadataProfileId) = ResolveDestination(addition);
        return _client.EnsureAlbum(new LibraryAdditionRequest(
            addition.ArtistMusicBrainzId,
            addition.AlbumMusicBrainzId,
            addition.ReleaseMusicBrainzId,
            rootFolderId,
            qualityProfileId,
            metadataProfileId));
    }

    public LibraryAdditionCreatedDto TryAddTaggedAlbum(LibraryAdditionDto addition, AiImportSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (TryResolveExistingArtistDestination(addition, out var existing))
            return EnsureAlbum(addition, existing);

        RootFolder root;
        try
        {
            root = _rootFolders.Get(settings.YoloRootFolderId, timeout: true);
        }
        catch (Exception exception)
        {
            throw new InvalidOperationException("The configured YOLO root folder is missing or inaccessible.", exception);
        }
        if (!root.Accessible || string.IsNullOrWhiteSpace(root.Path) || !Path.IsPathFullyQualified(root.Path))
            throw new InvalidOperationException("The configured YOLO root folder is missing or inaccessible.");
        var quality = _qualityProfiles.All().SingleOrDefault(x => x.Id == settings.YoloQualityProfileId)
            ?? throw new InvalidOperationException("The configured YOLO quality profile is missing.");
        var metadata = _metadataProfiles.All().SingleOrDefault(x => x.Id == settings.YoloMetadataProfileId)
            ?? throw new InvalidOperationException("The configured YOLO metadata profile is missing.");
        return EnsureAlbum(addition, (root.Id, quality.Id, metadata.Id));
    }

    private (int RootFolderId, int QualityProfileId, int MetadataProfileId) ResolveDestination(LibraryAdditionDto addition)
    {
        if (TryResolveExistingArtistDestination(addition, out var existing)) return existing;

        var root = _rootFolders.All().FirstOrDefault(x =>
            x.Accessible && !string.IsNullOrWhiteSpace(x.Path) && Path.IsPathFullyQualified(x.Path))
            ?? throw new InvalidOperationException("YOLO mode needs an accessible Lidarr root folder.");
        var quality = _qualityProfiles.All().FirstOrDefault(x =>
            string.Equals(x.Name, "Default", StringComparison.OrdinalIgnoreCase))
            ?? _qualityProfiles.All().FirstOrDefault()
            ?? throw new InvalidOperationException("YOLO mode needs a Lidarr quality profile.");
        var metadata = _metadataProfiles.All().FirstOrDefault(x =>
            string.Equals(x.Name, "Default", StringComparison.OrdinalIgnoreCase))
            ?? _metadataProfiles.All().FirstOrDefault()
            ?? throw new InvalidOperationException("YOLO mode needs a Lidarr metadata profile.");
        return (root.Id, quality.Id, metadata.Id);
    }

    private bool TryResolveExistingArtistDestination(
        LibraryAdditionDto addition,
        out (int RootFolderId, int QualityProfileId, int MetadataProfileId) destination)
    {
        destination = default;
        Artist? artist;
        try
        {
            artist = _artists.FindById(addition.ArtistMusicBrainzId);
        }
        catch (Exception)
        {
            return false;
        }

        if (artist is null || artist.QualityProfileId <= 0 || artist.MetadataProfileId <= 0 ||
            string.IsNullOrWhiteSpace(artist.RootFolderPath))
            return false;

        var root = _rootFolders.All().FirstOrDefault(x =>
            x.Accessible && !string.IsNullOrWhiteSpace(x.Path) && Path.IsPathFullyQualified(x.Path) &&
            string.Equals(NormalizeRoot(x.Path), NormalizeRoot(artist.RootFolderPath), StringComparison.Ordinal));
        if (root is null) return false;

        destination = (root.Id, artist.QualityProfileId, artist.MetadataProfileId);
        return true;
    }

    private LibraryAdditionCreatedDto EnsureAlbum(
        LibraryAdditionDto addition,
        (int RootFolderId, int QualityProfileId, int MetadataProfileId) destination) =>
        _client.EnsureAlbum(new LibraryAdditionRequest(
            addition.ArtistMusicBrainzId,
            addition.AlbumMusicBrainzId,
            addition.ReleaseMusicBrainzId,
            destination.RootFolderId,
            destination.QualityProfileId,
            destination.MetadataProfileId));

    private static string NormalizeRoot(string path) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
}
