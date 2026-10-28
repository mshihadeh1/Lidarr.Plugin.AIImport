using System.Collections.Concurrent;
using System.Text.Json.Nodes;

namespace Lidarr.AIImport.Companion.Library;

public sealed record AlbumLookupResource(Guid ReleaseGroupId, Guid ArtistId, JsonObject Payload);
public sealed record ExistingAlbum(int AlbumId, Guid ReleaseGroupId, int ArtistId, Guid ArtistMusicBrainzId, IReadOnlyList<Guid> ReleaseIds);
public sealed record RootFolderSettings(int Id, string Path, bool Accessible, int DefaultQualityProfileId);
public sealed record MetadataProfileSettings(int Id, string Name);
public sealed record EnsuredAlbum(int AlbumId, int ArtistId, Guid ReleaseGroupId, Guid ReleaseId, bool Created);

public interface ILidarrLibraryClient
{
    Task<ExistingAlbum?> FindAlbumAsync(Guid releaseGroupId, CancellationToken cancellationToken);
    Task<AlbumLookupResource> LookupAlbumAsync(Guid releaseGroupId, CancellationToken cancellationToken);
    Task<RootFolderSettings> GetRootFolderAsync(int rootFolderId, CancellationToken cancellationToken);
    Task<IReadOnlyList<MetadataProfileSettings>> GetMetadataProfilesAsync(CancellationToken cancellationToken);
    Task<ExistingAlbum> AddAlbumAsync(JsonObject payload, CancellationToken cancellationToken);
}

public interface IMissingAlbumExecutor
{
    Task<EnsuredAlbum> EnsureAsync(Guid artistId, Guid releaseGroupId, Guid releaseId, int rootFolderId, CancellationToken cancellationToken);
}

public sealed class MissingAlbumService : IMissingAlbumExecutor
{
    private static readonly ConcurrentDictionary<Guid, SemaphoreSlim> Locks = new();
    private readonly ILidarrLibraryClient _lidarr;

    public MissingAlbumService(ILidarrLibraryClient lidarr)
    {
        _lidarr = lidarr;
    }

    public async Task<EnsuredAlbum> EnsureAsync(
        Guid artistId,
        Guid releaseGroupId,
        Guid releaseId,
        int rootFolderId,
        CancellationToken cancellationToken)
    {
        if (artistId == Guid.Empty || releaseGroupId == Guid.Empty || releaseId == Guid.Empty)
            throw new ArgumentException("MusicBrainz artist, release-group, and release IDs are required.");
        if (rootFolderId <= 0) throw new ArgumentOutOfRangeException(nameof(rootFolderId));

        var gate = Locks.GetOrAdd(releaseGroupId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var existing = await _lidarr.FindAlbumAsync(releaseGroupId, cancellationToken).ConfigureAwait(false);
            if (existing is not null) return Verified(existing, artistId, releaseId, false);

            var lookup = await _lidarr.LookupAlbumAsync(releaseGroupId, cancellationToken).ConfigureAwait(false);
            if (lookup.ReleaseGroupId != releaseGroupId)
                throw new InvalidDataException("Lidarr album lookup returned a different release group.");
            if (lookup.ArtistId != artistId)
                throw new InvalidDataException("Lidarr album lookup returned a different MusicBrainz artist.");
            var root = await _lidarr.GetRootFolderAsync(rootFolderId, cancellationToken).ConfigureAwait(false);
            if (!root.Accessible || root.Id != rootFolderId || root.DefaultQualityProfileId <= 0 || string.IsNullOrWhiteSpace(root.Path))
                throw new InvalidOperationException("The configured Lidarr root folder is not accessible or has no default quality profile.");
            var noneProfiles = (await _lidarr.GetMetadataProfilesAsync(cancellationToken).ConfigureAwait(false))
                .Where(x => string.Equals(x.Name, "None", StringComparison.OrdinalIgnoreCase) && x.Id > 0)
                .ToArray();
            if (noneProfiles.Length != 1)
                throw new InvalidOperationException("Exactly one Lidarr metadata profile named None is required for a newly created album artist.");

            var payload = BuildPayload(lookup, releaseId, root, noneProfiles[0]);
            ExistingAlbum added;
            try
            {
                added = await _lidarr.AddAlbumAsync(payload, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
            {
                added = await _lidarr.FindAlbumAsync(releaseGroupId, cancellationToken).ConfigureAwait(false)
                    ?? throw new InvalidOperationException("Lidarr album creation outcome is indeterminate and canonical reconciliation found no album.", exception);
            }
            return Verified(added, artistId, releaseId, true);
        }
        finally
        {
            gate.Release();
        }
    }

    private static JsonObject BuildPayload(
        AlbumLookupResource lookup,
        Guid releaseId,
        RootFolderSettings root,
        MetadataProfileSettings noneProfile)
    {
        var payload = lookup.Payload.DeepClone().AsObject();
        if (!Guid.TryParse(payload["foreignAlbumId"]?.GetValue<string>(), out var payloadGroup) || payloadGroup != lookup.ReleaseGroupId)
            throw new InvalidDataException("Lidarr lookup payload has an invalid release-group identity.");
        var releases = payload["releases"] as JsonArray
            ?? throw new InvalidDataException("Lidarr lookup payload has no releases.");
        var selected = 0;
        foreach (var node in releases)
        {
            var release = node as JsonObject ?? throw new InvalidDataException("Lidarr lookup contained a malformed release.");
            var match = Guid.TryParse(release["foreignReleaseId"]?.GetValue<string>(), out var candidateId) && candidateId == releaseId;
            release["monitored"] = match;
            if (match) selected++;
        }
        if (selected != 1) throw new InvalidDataException("The tagged MusicBrainz release was not uniquely present in Lidarr lookup results.");
        payload["monitored"] = true;
        payload["anyReleaseOk"] = false;
        payload["addOptions"] = new JsonObject { ["searchForNewAlbum"] = false };

        var artist = payload["artist"] as JsonObject
            ?? throw new InvalidDataException("Lidarr lookup payload has no artist.");
        if (!Guid.TryParse(artist["foreignArtistId"]?.GetValue<string>(), out var artistId) || artistId != lookup.ArtistId)
            throw new InvalidDataException("Lidarr lookup payload has an invalid artist identity.");
        artist["rootFolderPath"] = root.Path;
        artist["qualityProfileId"] = root.DefaultQualityProfileId;
        artist["metadataProfileId"] = noneProfile.Id;
        artist["monitored"] = false;
        artist["monitorNewItems"] = "none";
        artist["tags"] = new JsonArray();
        artist["addOptions"] = new JsonObject
        {
            ["monitor"] = "none",
            ["searchForMissingAlbums"] = false
        };
        return payload;
    }

    private static EnsuredAlbum Verified(ExistingAlbum album, Guid artistId, Guid releaseId, bool created)
    {
        if (album.AlbumId <= 0 || album.ArtistId <= 0 || album.ReleaseGroupId == Guid.Empty || album.ArtistMusicBrainzId == Guid.Empty)
            throw new InvalidDataException("Lidarr returned invalid album ownership identifiers.");
        if (!album.ReleaseIds.Contains(releaseId))
            throw new InvalidDataException("Lidarr album does not contain the requested MusicBrainz release.");
        if (album.ArtistMusicBrainzId != artistId)
            throw new InvalidDataException("Lidarr album belongs to a different MusicBrainz artist.");
        return new EnsuredAlbum(album.AlbumId, album.ArtistId, album.ReleaseGroupId, releaseId, created);
    }
}
