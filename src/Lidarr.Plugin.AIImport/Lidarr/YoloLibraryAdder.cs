using Lidarr.Plugin.AIImport.Approval;
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

    private (int RootFolderId, int QualityProfileId, int MetadataProfileId) ResolveDestination(LibraryAdditionDto addition)
    {
        Artist? existingArtist = null;
        try
        {
            existingArtist = _artists.FindById(addition.ArtistMusicBrainzId);
        }
        catch (Exception)
        {
            existingArtist = null;
        }

        if (existingArtist is not null &&
            existingArtist.QualityProfileId > 0 && existingArtist.MetadataProfileId > 0 &&
            !string.IsNullOrWhiteSpace(existingArtist.RootFolderPath))
        {
            var existingRoot = _rootFolders.All().FirstOrDefault(x =>
                string.Equals(
                    NormalizeRoot(x.Path),
                    NormalizeRoot(existingArtist.RootFolderPath),
                    StringComparison.Ordinal));
            if (existingRoot is not null && existingRoot.Accessible)
            {
                return (existingRoot.Id, existingArtist.QualityProfileId, existingArtist.MetadataProfileId);
            }
        }

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

    private static string NormalizeRoot(string path) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
}
