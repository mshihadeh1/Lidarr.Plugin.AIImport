using System.Security.Cryptography;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Lidarr.AIImport.Companion.Approval;
using Lidarr.AIImport.Companion.Configuration;
using Lidarr.AIImport.Companion.Execution;
using Lidarr.AIImport.Companion.Library;
using Lidarr.AIImport.Companion.Models;
using Lidarr.AIImport.Companion.Safety;

namespace Lidarr.AIImport.Companion.Lidarr;

public interface ILidarrClient
{
    Task<LidarrSystemStatus> GetSystemStatusAsync(CancellationToken cancellationToken);
    Task<IReadOnlyList<QueueCandidate>> GetQueueAsync(CancellationToken cancellationToken);
    Task<IReadOnlyList<SourceFileCandidate>> GetManualImportsAsync(string? downloadId, string? folder, CancellationToken cancellationToken);
    Task<IReadOnlyList<ReleaseCandidate>> GetReleaseCandidatesAsync(int albumId, CancellationToken cancellationToken);
    Task<ImportSafetyConfiguration> GetImportSafetyConfigurationAsync(CancellationToken cancellationToken);
}

public sealed record LidarrCommandReceipt(int CommandId, string Status);

public interface ILidarrExecutionClient
{
    Task<string> GetArtistPathAsync(int artistId, CancellationToken cancellationToken);
    Task RevalidateManualImportAsync(ApprovedImportPlan plan, CancellationToken cancellationToken);
    Task<LidarrCommandReceipt> SubmitManualImportAsync(ApprovedImportPlan plan, CancellationToken cancellationToken);
}

public sealed class LidarrClient : ILidarrClient, ILidarrLibraryClient, ILidarrExecutionClient, ILidarrReconciliationClient
{
    private static readonly JsonDocumentOptions DocumentOptions = new() { AllowTrailingCommas = false };
    private readonly HttpClient _httpClient;
    private readonly CompanionOptions _options;

    public LidarrClient(HttpClient httpClient, CompanionOptions options)
    {
        _httpClient = httpClient;
        _options = options;
    }

    public async Task<LidarrSystemStatus> GetSystemStatusAsync(CancellationToken cancellationToken)
    {
        using var document = await GetJsonAsync("api/v1/system/status", cancellationToken);
        return new LidarrSystemStatus(
            String(document.RootElement, "version"),
            String(document.RootElement, "packageVersion"),
            String(document.RootElement, "branch"));
    }

    public async Task<IReadOnlyList<QueueCandidate>> GetQueueAsync(CancellationToken cancellationToken)
    {
        var result = new List<QueueCandidate>();
        var seenRecords = 0;
        const int pageSize = 100;
        for (var page = 1; page <= 100; page++)
        {
            using var document = await GetJsonAsync($"api/v1/queue?page={page}&pageSize={pageSize}&includeUnknownArtistItems=true&includeArtist=true&includeAlbum=true", cancellationToken);
            var records = document.RootElement.GetProperty("records");
            seenRecords += records.GetArrayLength();
            foreach (var item in records.EnumerateArray())
            {
                var downloadId = String(item, "downloadId");
                if (string.IsNullOrWhiteSpace(downloadId))
                {
                    continue;
                }
                result.Add(new QueueCandidate(
                    Int(item, "id"),
                    NullableInt(item, "albumId"),
                    String(item, "title"),
                    String(item, "status"),
                    NullableString(item, "trackedDownloadState"),
                    downloadId,
                    NullableString(item, "outputPath"),
                    ReadStatusMessages(item)));
            }

            var totalRecords = Int(document.RootElement, "totalRecords");
            if (totalRecords > 10_000) throw new InvalidDataException("Lidarr queue exceeds the discovery safety limit.");
            if (seenRecords >= totalRecords || records.GetArrayLength() == 0) return result;
        }
        throw new InvalidDataException("Lidarr queue pagination exceeded the discovery safety limit.");
    }

    public async Task<IReadOnlyList<SourceFileCandidate>> GetManualImportsAsync(string? downloadId, string? folder, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(downloadId) == string.IsNullOrWhiteSpace(folder))
        {
            throw new ArgumentException("Exactly one of downloadId or folder is required.");
        }
        var query = !string.IsNullOrWhiteSpace(downloadId)
            ? $"downloadId={Uri.EscapeDataString(downloadId)}"
            : $"folder={Uri.EscapeDataString(folder!)}";
        using var document = await GetJsonAsync($"api/v1/manualimport?{query}&filterExistingFiles=false&replaceExistingFiles=false", cancellationToken);
        var result = new List<SourceFileCandidate>();
        foreach (var item in document.RootElement.EnumerateArray())
        {
            var path = String(item, "path");
            var tags = item.TryGetProperty("audioTags", out var tagElement) && tagElement.ValueKind == JsonValueKind.Object
                ? tagElement
                : default;
            var trackNumbers = tags.ValueKind == JsonValueKind.Object && tags.TryGetProperty("trackNumbers", out var numbers) && numbers.ValueKind == JsonValueKind.Array
                ? numbers.EnumerateArray().Where(x => x.TryGetInt32(out _)).Select(x => x.GetInt32()).ToArray()
                : Array.Empty<int>();
            result.Add(new SourceFileCandidate(
                OpaqueId(!string.IsNullOrWhiteSpace(downloadId) ? downloadId! : folder!, path),
                String(item, "name"),
                TagOrNested(tags, "artistTitle", item, "artist", "artistName"),
                TagOrNested(tags, "albumTitle", item, "album", "title"),
                tags.ValueKind == JsonValueKind.Object ? String(tags, "title") : string.Empty,
                tags.ValueKind == JsonValueKind.Object ? Int(tags, "discNumber") : 0,
                trackNumbers,
                tags.ValueKind == JsonValueKind.Object ? DurationMilliseconds(tags) : 0,
                tags.ValueKind == JsonValueKind.Object ? NullableString(tags, "trackMBId") : null,
                ReadRejections(item),
                NestedInt(item, "artist", "id"),
                NestedInt(item, "album", "id"),
                tags.ValueKind == JsonValueKind.Object ? NullableString(tags, "artistMBId") : null,
                tags.ValueKind == JsonValueKind.Object ? NullableString(tags, "albumMBId") : null,
                tags.ValueKind == JsonValueKind.Object ? NullableString(tags, "releaseMBId") : null,
                tags.ValueKind == JsonValueKind.Object ? NullableString(tags, "recordingMBId") : null,
                path,
                item.TryGetProperty("quality", out var quality) && quality.ValueKind == JsonValueKind.Object
                    ? JsonNode.Parse(quality.GetRawText())?.AsObject()
                    : null,
                Int(item, "indexerFlags"),
                NullableString(item, "downloadId") ?? downloadId,
                item.TryGetProperty("size", out var size) && size.TryGetInt64(out var length) ? length : 0,
                RequiredPositiveInt(item, "id")));
        }
        if (result.Select(x => x.Id).Distinct(StringComparer.Ordinal).Count() != result.Count)
        {
            throw new InvalidDataException("Lidarr returned duplicate manual-import source identities.");
        }
        return result;
    }

    public async Task<IReadOnlyList<ReleaseCandidate>> GetReleaseCandidatesAsync(int albumId, CancellationToken cancellationToken)
    {
        using var albumDocument = await GetJsonAsync($"api/v1/album/{albumId}", cancellationToken);
        var album = albumDocument.RootElement;
        if (RequiredPositiveInt(album, "id") != albumId)
            throw new InvalidDataException("Lidarr album response did not match the requested album.");
        var albumTitle = String(album, "title");
        var artist = album.TryGetProperty("artist", out var artistElement) ? String(artistElement, "artistName") : string.Empty;
        var artistId = album.TryGetProperty("artist", out artistElement) && artistElement.ValueKind == JsonValueKind.Object
            ? RequiredPositiveInt(artistElement, "id")
            : throw new InvalidDataException("Lidarr album response is missing its artist owner.");
        var releases = album.GetProperty("releases").EnumerateArray().ToArray();
        var result = new List<ReleaseCandidate>();
        foreach (var release in releases)
        {
            var releaseId = RequiredPositiveInt(release, "id");
            if (RequiredPositiveInt(release, "albumId") != albumId)
                throw new InvalidDataException("Lidarr release does not belong to the requested album.");
            using var tracksDocument = await GetJsonAsync($"api/v1/track?albumReleaseId={releaseId}", cancellationToken);
            var tracks = tracksDocument.RootElement.EnumerateArray().Select(track =>
            {
                if (RequiredPositiveInt(track, "albumId") != albumId)
                    throw new InvalidDataException("Lidarr track does not belong to the requested album.");
                return new TrackCandidate(
                    RequiredPositiveInt(track, "id"),
                    String(track, "title"),
                    Int(track, "mediumNumber"),
                    String(track, "trackNumber"),
                    Int(track, "duration"),
                    NullableString(track, "foreignTrackId"),
                    NullableString(track, "foreignRecordingId"),
                    RequiredBool(track, "hasFile"));
            }).ToArray();
            if (tracks.Select(x => x.Id).Distinct().Count() != tracks.Length)
                throw new InvalidDataException("Lidarr returned duplicate track IDs for a release.");
            result.Add(new ReleaseCandidate(
                albumId,
                releaseId,
                artist,
                String(release, "title"),
                NullableString(release, "foreignReleaseId"),
                albumTitle,
                String(release, "disambiguation"),
                tracks,
                artistId));
        }
        if (result.Select(x => x.Id).Distinct().Count() != result.Count)
            throw new InvalidDataException("Lidarr returned duplicate release IDs for an album.");
        return result;
    }

    public async Task<ImportSafetyConfiguration> GetImportSafetyConfigurationAsync(CancellationToken cancellationToken)
    {
        using var mediaDocument = await GetJsonAsync("api/v1/config/mediamanagement", cancellationToken);
        using var metadataDocument = await GetJsonAsync("api/v1/config/metadataprovider", cancellationToken);
        var media = mediaDocument.RootElement;
        var metadata = metadataDocument.RootElement;
        return new ImportSafetyConfiguration(
            RequiredBool(media, "copyUsingHardlinks"),
            RequiredBool(media, "useScriptImport"),
            String(media, "scriptImportPath"),
            RequiredBool(media, "setPermissionsLinux"),
            RequiredString(media, "fileDate"),
            RequiredBool(media, "importExtraFiles"),
            RequiredString(metadata, "writeAudioTags"),
            RequiredBool(metadata, "scrubAudioTags"),
            RequiredBool(metadata, "embedCoverArt"));
    }

    public async Task<ExistingAlbum?> FindAlbumAsync(Guid releaseGroupId, CancellationToken cancellationToken)
    {
        using var document = await GetJsonAsync(
            $"api/v1/album?foreignAlbumId={Uri.EscapeDataString(releaseGroupId.ToString())}", cancellationToken);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("Lidarr album lookup response must be an array.");
        var matches = document.RootElement.EnumerateArray().ToArray();
        if (matches.Length == 0) return null;
        if (matches.Length != 1) throw new InvalidDataException("Lidarr returned duplicate albums for one MusicBrainz release group.");
        return ParseExistingAlbum(matches[0], releaseGroupId);
    }

    public async Task<AlbumLookupResource> LookupAlbumAsync(Guid releaseGroupId, CancellationToken cancellationToken)
    {
        using var document = await GetJsonAsync(
            $"api/v1/album/lookup?term={Uri.EscapeDataString("lidarr:" + releaseGroupId)}", cancellationToken);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("Lidarr album metadata lookup response must be an array.");
        var matches = document.RootElement.EnumerateArray()
            .Where(item => Guid.TryParse(String(item, "foreignAlbumId"), out var id) && id == releaseGroupId)
            .ToArray();
        if (matches.Length != 1)
            throw new InvalidDataException("Lidarr did not return exactly one authoritative lookup result for the MusicBrainz release group.");
        var artist = matches[0].GetProperty("artist");
        if (!Guid.TryParse(RequiredString(artist, "foreignArtistId"), out var artistId) || artistId == Guid.Empty)
            throw new InvalidDataException("Lidarr album lookup result has no valid MusicBrainz artist ID.");
        var payload = JsonNode.Parse(matches[0].GetRawText())?.AsObject()
            ?? throw new InvalidDataException("Lidarr album lookup result could not be preserved.");
        return new AlbumLookupResource(releaseGroupId, artistId, payload);
    }

    public async Task<RootFolderSettings> GetRootFolderAsync(int rootFolderId, CancellationToken cancellationToken)
    {
        using var document = await GetJsonAsync("api/v1/rootfolder", cancellationToken);
        var matches = document.RootElement.EnumerateArray()
            .Where(item => RequiredPositiveInt(item, "id") == rootFolderId)
            .ToArray();
        if (matches.Length != 1) throw new InvalidDataException("Configured Lidarr root folder was not found uniquely.");
        var item = matches[0];
        return new RootFolderSettings(
            rootFolderId,
            RequiredString(item, "path"),
            RequiredBool(item, "accessible"),
            RequiredPositiveInt(item, "defaultQualityProfileId"));
    }

    public async Task<IReadOnlyList<MetadataProfileSettings>> GetMetadataProfilesAsync(CancellationToken cancellationToken)
    {
        using var document = await GetJsonAsync("api/v1/metadataprofile", cancellationToken);
        return document.RootElement.EnumerateArray()
            .Select(item => new MetadataProfileSettings(RequiredPositiveInt(item, "id"), RequiredString(item, "name")))
            .ToArray();
    }

    public async Task<ExistingAlbum> AddAlbumAsync(JsonObject payload, CancellationToken cancellationToken)
    {
        using var document = await SendJsonAsync(HttpMethod.Post, "api/v1/album", payload, cancellationToken);
        if (!Guid.TryParse(RequiredString(document.RootElement, "foreignAlbumId"), out var releaseGroupId))
            throw new InvalidDataException("Created Lidarr album has no valid release-group identity.");
        return ParseExistingAlbum(document.RootElement, releaseGroupId);
    }

    public async Task<string> GetArtistPathAsync(int artistId, CancellationToken cancellationToken)
    {
        if (artistId <= 0) throw new ArgumentOutOfRangeException(nameof(artistId));
        using var document = await GetJsonAsync($"api/v1/artist/{artistId}", cancellationToken).ConfigureAwait(false);
        var root = document.RootElement;
        if (RequiredPositiveInt(root, "id") != artistId)
            throw new InvalidDataException("Lidarr artist response did not match the approved artist.");
        var path = RequiredString(root, "path");
        if (!Path.IsPathFullyQualified(path))
            throw new InvalidDataException("Lidarr artist path must be absolute.");
        return path;
    }

    public async Task RevalidateManualImportAsync(
        ApprovedImportPlan plan,
        CancellationToken cancellationToken)
    {
        var request = new JsonArray();
        foreach (var file in plan.Files)
        {
            request.Add(new JsonObject
            {
                ["id"] = file.ManualImportId,
                ["path"] = file.SourcePath,
                ["name"] = Path.GetFileNameWithoutExtension(file.SourcePath),
                ["artistId"] = plan.ArtistId,
                ["albumId"] = plan.AlbumId,
                ["albumReleaseId"] = plan.ReleaseId,
                ["trackIds"] = new JsonArray(file.TrackId),
                ["quality"] = file.Quality.DeepClone(),
                ["releaseGroup"] = string.Empty,
                ["indexerFlags"] = file.IndexerFlags,
                ["downloadId"] = file.DownloadId,
                ["additionalFile"] = false,
                ["replaceExistingFiles"] = false,
                ["disableReleaseSwitching"] = false
            });
        }
        using var document = await SendJsonAsync(HttpMethod.Post, "api/v1/manualimport", request, cancellationToken).ConfigureAwait(false);
        if (document.RootElement.ValueKind != JsonValueKind.Array || document.RootElement.GetArrayLength() != plan.Files.Count)
            throw new InvalidDataException("Lidarr manual-import revalidation returned an unexpected file count.");
        var approvedByPath = plan.Files.ToDictionary(x => x.SourcePath, StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in document.RootElement.EnumerateArray())
        {
            var path = RequiredString(item, "path");
            if (!approvedByPath.TryGetValue(path, out var approved) || !seen.Add(path))
                throw new InvalidDataException("Lidarr manual-import revalidation returned an unapproved or duplicate path.");
            var validSize = item.TryGetProperty("size", out var size) && size.TryGetInt64(out var length) && length == approved.Size;
            var validQuality = item.TryGetProperty("quality", out var quality) && quality.ValueKind == JsonValueKind.Object &&
                JsonNode.DeepEquals(JsonNode.Parse(quality.GetRawText()), approved.Quality);
            if (RequiredPositiveInt(item, "id") != approved.ManualImportId ||
                RequiredPositiveInt(item.GetProperty("artist"), "id") != plan.ArtistId ||
                RequiredPositiveInt(item.GetProperty("album"), "id") != plan.AlbumId ||
                RequiredPositiveInt(item, "albumReleaseId") != plan.ReleaseId || !validSize || !validQuality ||
                Int(item, "indexerFlags") != approved.IndexerFlags ||
                !string.Equals(RequiredString(item, "downloadId"), approved.DownloadId, StringComparison.Ordinal) ||
                RequiredBool(item, "additionalFile") || RequiredBool(item, "replaceExistingFiles") ||
                RequiredBool(item, "disableReleaseSwitching"))
                throw new InvalidDataException("Lidarr manual-import revalidation changed approved execution material.");
            var rejections = item.GetProperty("rejections");
            if (rejections.ValueKind != JsonValueKind.Array || rejections.GetArrayLength() != 0)
                throw new InvalidDataException("Lidarr manual-import revalidation returned a rejection.");
            var tracks = item.GetProperty("tracks");
            if (tracks.ValueKind != JsonValueKind.Array || tracks.GetArrayLength() != 1 ||
                RequiredPositiveInt(tracks[0], "id") != approved.TrackId)
                throw new InvalidDataException("Lidarr manual-import revalidation changed the approved track mapping.");
        }
    }

    public async Task<LidarrCommandReceipt> SubmitManualImportAsync(
        ApprovedImportPlan plan,
        CancellationToken cancellationToken)
    {
        if (plan.ArtistId <= 0 || plan.AlbumId <= 0 || plan.ReleaseId <= 0 || plan.Files.Count == 0)
            throw new ArgumentException("Approved import plan is incomplete.", nameof(plan));
        if (plan.Files.Select(x => x.SourcePath).Distinct(StringComparer.Ordinal).Count() != plan.Files.Count ||
            plan.Files.Select(x => x.TrackId).Distinct().Count() != plan.Files.Count)
            throw new ArgumentException("Approved import plan contains duplicate source paths or track IDs.", nameof(plan));
        var files = new JsonArray();
        foreach (var file in plan.Files)
        {
            if (file.TrackId <= 0 || !Path.IsPathFullyQualified(file.SourcePath) ||
                string.IsNullOrWhiteSpace(file.DownloadId) || file.Quality.Count == 0)
                throw new ArgumentException("Approved import file is incomplete.", nameof(plan));
            files.Add(new JsonObject
            {
                ["path"] = file.SourcePath,
                ["artistId"] = plan.ArtistId,
                ["albumId"] = plan.AlbumId,
                ["albumReleaseId"] = plan.ReleaseId,
                ["trackIds"] = new JsonArray(file.TrackId),
                ["quality"] = file.Quality.DeepClone(),
                ["indexerFlags"] = file.IndexerFlags,
                ["downloadId"] = file.DownloadId,
                ["disableReleaseSwitching"] = false
            });
        }
        var payload = new JsonObject
        {
            ["name"] = "ManualImport",
            ["files"] = files,
            ["importMode"] = "copy",
            ["replaceExistingFiles"] = false
        };
        using var document = await SendJsonAsync(HttpMethod.Post, "api/v1/command", payload, cancellationToken).ConfigureAwait(false);
        return new LidarrCommandReceipt(
            RequiredPositiveInt(document.RootElement, "id"),
            RequiredString(document.RootElement, "status"));
    }

    public async Task<LidarrCommandState> GetCommandAsync(int commandId, CancellationToken cancellationToken)
    {
        if (commandId <= 0) throw new ArgumentOutOfRangeException(nameof(commandId));
        using var document = await GetJsonAsync($"api/v1/command/{commandId}", cancellationToken).ConfigureAwait(false);
        var root = document.RootElement;
        return new LidarrCommandState(
            RequiredPositiveInt(root, "id"),
            RequiredString(root, "name"),
            RequiredString(root, "status").ToLowerInvariant(),
            RequiredString(root, "result").ToLowerInvariant());
    }

    public async Task<IReadOnlyList<LidarrImportHistoryEvidence>> GetImportHistoryAsync(
        string downloadId,
        int albumId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(downloadId)) throw new ArgumentException("Download ID is required.", nameof(downloadId));
        if (albumId <= 0) throw new ArgumentOutOfRangeException(nameof(albumId));
        var path = $"api/v1/history?page=1&pageSize=100&sortKey=date&sortDirection=descending&eventType=3&albumId={albumId}&downloadId={Uri.EscapeDataString(downloadId)}";
        using var document = await GetJsonAsync(path, cancellationToken).ConfigureAwait(false);
        var root = document.RootElement;
        if (RequiredPositiveInt(root, "page") != 1 || RequiredPositiveInt(root, "pageSize") != 100)
            throw new InvalidDataException("Lidarr history response used unexpected paging.");
        var totalRecords = Int(root, "totalRecords");
        if (totalRecords < 0 || totalRecords > 100)
            throw new InvalidDataException("Lidarr reconciliation history exceeded its safety bound.");
        var records = root.GetProperty("records");
        if (records.ValueKind != JsonValueKind.Array || records.GetArrayLength() != totalRecords)
            throw new InvalidDataException("Lidarr reconciliation history paging was incomplete.");
        var result = new List<LidarrImportHistoryEvidence>(records.GetArrayLength());
        foreach (var item in records.EnumerateArray())
        {
            var data = item.GetProperty("data");
            if (data.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException("Lidarr import history data is missing.");
            if (!DateTimeOffset.TryParse(RequiredString(item, "date"), out var date))
                throw new InvalidDataException("Lidarr import history date is invalid.");
            result.Add(new LidarrImportHistoryEvidence(
                RequiredPositiveInt(item, "id"),
                RequiredPositiveInt(item, "artistId"),
                RequiredPositiveInt(item, "albumId"),
                RequiredPositiveInt(item, "trackId"),
                RequiredString(item, "downloadId"),
                date,
                RequiredPositiveIntString(data, "fileId"),
                RequiredString(data, "droppedPath"),
                RequiredString(data, "importedPath"),
                RequiredNonNegativeLongString(data, "size"),
                RequiredIndexerFlagsString(data, "indexerFlags"),
                RequiredObject(item, "quality")));
        }
        return result;
    }

    public async Task<IReadOnlyList<LidarrTrackFileEvidence>> GetTrackFilesAsync(
        int albumId,
        IReadOnlyList<int> trackFileIds,
        CancellationToken cancellationToken)
    {
        if (albumId <= 0) throw new ArgumentOutOfRangeException(nameof(albumId));
        var ids = ValidateIds(trackFileIds, nameof(trackFileIds));
        using var document = await GetJsonAsync($"api/v1/trackfile?albumId={albumId}", cancellationToken).ConfigureAwait(false);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("Lidarr track-file response must be an array.");
        var requested = ids.ToHashSet();
        var result = document.RootElement.EnumerateArray().Select(item => new LidarrTrackFileEvidence(
            RequiredPositiveInt(item, "id"),
            RequiredPositiveInt(item, "artistId"),
            RequiredPositiveInt(item, "albumId"),
            RequiredString(item, "path"),
            RequiredNonNegativeLong(item, "size"),
            RequiredObject(item, "quality"),
            RequiredNonNegativeInt(item, "indexerFlags")))
            .Where(item => requested.Contains(item.TrackFileId))
            .ToArray();
        return result;
    }

    public async Task<IReadOnlyList<LidarrTrackBinding>> GetTracksAsync(
        IReadOnlyList<int> trackIds,
        CancellationToken cancellationToken)
    {
        var ids = ValidateIds(trackIds, nameof(trackIds));
        var query = string.Join("&", ids.Select(id => $"trackIds={id}"));
        using var document = await GetJsonAsync($"api/v1/track?{query}", cancellationToken).ConfigureAwait(false);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("Lidarr track response must be an array.");
        return document.RootElement.EnumerateArray().Select(item => new LidarrTrackBinding(
            RequiredPositiveInt(item, "id"),
            RequiredPositiveInt(item, "artistId"),
            RequiredPositiveInt(item, "albumId"),
            RequiredPositiveInt(item, "trackFileId"),
            RequiredBool(item, "hasFile"))).ToArray();
    }

    private async Task<JsonDocument> GetJsonAsync(string relativePath, CancellationToken cancellationToken)
        => await SendJsonAsync(HttpMethod.Get, relativePath, null, cancellationToken).ConfigureAwait(false);

    private async Task<JsonDocument> SendJsonAsync(HttpMethod method, string relativePath, JsonNode? payload, CancellationToken cancellationToken)
    {
        var baseUri = _options.LidarrBaseUrl.TrimEnd('/') + "/";
        using var request = new HttpRequestMessage(method, new Uri(new Uri(baseUri), relativePath));
        request.Headers.Add("X-Api-Key", _options.LidarrApiKey);
        if (payload is not null)
            request.Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json");
        using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        return await JsonDocument.ParseAsync(stream, DocumentOptions, cancellationToken).ConfigureAwait(false);
    }

    private static ExistingAlbum ParseExistingAlbum(JsonElement item, Guid expectedReleaseGroupId)
    {
        if (!Guid.TryParse(RequiredString(item, "foreignAlbumId"), out var releaseGroupId) || releaseGroupId != expectedReleaseGroupId)
            throw new InvalidDataException("Lidarr album response has the wrong MusicBrainz release-group identity.");
        var artist = item.GetProperty("artist");
        if (!Guid.TryParse(RequiredString(artist, "foreignArtistId"), out var artistMusicBrainzId) || artistMusicBrainzId == Guid.Empty)
            throw new InvalidDataException("Lidarr album response has no valid MusicBrainz artist identity.");
        var releases = item.GetProperty("releases").EnumerateArray()
            .Select(release => Guid.TryParse(RequiredString(release, "foreignReleaseId"), out var id)
                ? id
                : throw new InvalidDataException("Lidarr album response contains an invalid release identity."))
            .ToArray();
        if (releases.Length == 0 || releases.Distinct().Count() != releases.Length)
            throw new InvalidDataException("Lidarr album response has missing or duplicate release identities.");
        return new ExistingAlbum(
            RequiredPositiveInt(item, "id"),
            releaseGroupId,
            RequiredPositiveInt(artist, "id"),
            artistMusicBrainzId,
            releases);
    }

    private static string OpaqueId(string group, string path)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(group + "\n" + path));
        return Convert.ToHexString(bytes).ToLowerInvariant()[..20];
    }

    private static IReadOnlyList<string> ReadStatusMessages(JsonElement item)
    {
        if (!item.TryGetProperty("statusMessages", out var values) || values.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<string>();
        }
        var result = new List<string>();
        foreach (var value in values.EnumerateArray())
        {
            if (value.ValueKind == JsonValueKind.String)
            {
                result.Add(value.GetString()!);
            }
            else if (value.ValueKind == JsonValueKind.Object)
            {
                var title = String(value, "title");
                if (!string.IsNullOrWhiteSpace(title)) result.Add(title);
                if (value.TryGetProperty("messages", out var messages) && messages.ValueKind == JsonValueKind.Array)
                {
                    result.AddRange(messages.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!));
                }
            }
        }
        return result;
    }

    private static IReadOnlyList<string> ReadRejections(JsonElement item)
    {
        if (!item.TryGetProperty("rejections", out var values) || values.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<string>();
        }
        return values.EnumerateArray().Select(x => x.ValueKind == JsonValueKind.String ? x.GetString()! : String(x, "reason"))
            .Where(x => !string.IsNullOrWhiteSpace(x)).ToArray();
    }

    private static int DurationMilliseconds(JsonElement tags)
    {
        if (!tags.TryGetProperty("duration", out var value)) return 0;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var milliseconds)) return milliseconds;
        if (value.ValueKind == JsonValueKind.String && TimeSpan.TryParse(value.GetString(), out var duration)) return checked((int)duration.TotalMilliseconds);
        return 0;
    }

    private static string TagOrNested(JsonElement tags, string tagName, JsonElement item, string objectName, string valueName)
    {
        var tagged = tags.ValueKind == JsonValueKind.Object ? String(tags, tagName) : string.Empty;
        if (!string.IsNullOrWhiteSpace(tagged)) return tagged;
        return item.TryGetProperty(objectName, out var nested) && nested.ValueKind == JsonValueKind.Object ? String(nested, valueName) : string.Empty;
    }

    private static string String(JsonElement item, string name) => item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? string.Empty : string.Empty;
    private static string? NullableString(JsonElement item, string name) { var value = String(item, name); return string.IsNullOrWhiteSpace(value) ? null : value; }
    private static int Int(JsonElement item, string name) => item.TryGetProperty(name, out var value) && value.TryGetInt32(out var result) ? result : 0;
    private static int? NullableInt(JsonElement item, string name) => item.TryGetProperty(name, out var value) && value.TryGetInt32(out var result) ? result : null;
    private static int? NestedInt(JsonElement item, string objectName, string valueName) =>
        item.TryGetProperty(objectName, out var nested) && nested.ValueKind == JsonValueKind.Object ? NullableInt(nested, valueName) : null;
    private static int RequiredPositiveInt(JsonElement item, string name) =>
        item.TryGetProperty(name, out var value) && value.TryGetInt32(out var result) && result > 0
            ? result
            : throw new InvalidDataException($"Lidarr response field {name} must be a positive integer.");
    private static int[] ValidateIds(IReadOnlyList<int> ids, string parameterName)
    {
        if (ids.Count == 0 || ids.Count > 100 || ids.Any(id => id <= 0) || ids.Distinct().Count() != ids.Count)
            throw new ArgumentException("IDs must contain between one and 100 unique positive values.", parameterName);
        return ids.OrderBy(id => id).ToArray();
    }
    private static JsonObject RequiredObject(JsonElement item, string name) =>
        item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Object
            ? JsonNode.Parse(value.GetRawText())?.AsObject()
                ?? throw new InvalidDataException($"Lidarr response field {name} is invalid.")
            : throw new InvalidDataException($"Lidarr response field {name} must be an object.");
    private static long RequiredNonNegativeLong(JsonElement item, string name) =>
        item.TryGetProperty(name, out var value) && value.TryGetInt64(out var result) && result >= 0
            ? result
            : throw new InvalidDataException($"Lidarr response field {name} must be a nonnegative integer.");
    private static int RequiredNonNegativeInt(JsonElement item, string name) =>
        item.TryGetProperty(name, out var value) && value.TryGetInt32(out var result) && result >= 0
            ? result
            : throw new InvalidDataException($"Lidarr response field {name} must be a nonnegative integer.");
    private static int RequiredPositiveIntString(JsonElement item, string name) =>
        int.TryParse(RequiredString(item, name), NumberStyles.None, CultureInfo.InvariantCulture, out var result) && result > 0
            ? result
            : throw new InvalidDataException($"Lidarr response field {name} must be a positive integer string.");
    private static int RequiredIndexerFlagsString(JsonElement item, string name)
    {
        var text = RequiredString(item, name);
        if (int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var numeric))
        {
            if (numeric is >= 0 and <= 127) return numeric;
            throw new InvalidDataException($"Lidarr response field {name} contains unsupported indexer flags.");
        }

        var known = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["Freeleech"] = 1,
            ["Halfleech"] = 2,
            ["DoubleUpload"] = 4,
            ["Internal"] = 8,
            ["Scene"] = 16,
            ["Freeleech75"] = 32,
            ["Freeleech25"] = 64
        };
        var parts = text.Split(", ", StringSplitOptions.None);
        var result = 0;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var part in parts)
        {
            if (!known.TryGetValue(part, out var flag) || !seen.Add(part))
                throw new InvalidDataException($"Lidarr response field {name} contains unsupported indexer flags.");
            result |= flag;
        }
        return result;
    }
    private static int RequiredNonNegativeIntString(JsonElement item, string name) =>
        int.TryParse(RequiredString(item, name), NumberStyles.None, CultureInfo.InvariantCulture, out var result) && result >= 0
            ? result
            : throw new InvalidDataException($"Lidarr response field {name} must be a nonnegative integer string.");
    private static long RequiredNonNegativeLongString(JsonElement item, string name) =>
        long.TryParse(RequiredString(item, name), NumberStyles.None, CultureInfo.InvariantCulture, out var result) && result >= 0
            ? result
            : throw new InvalidDataException($"Lidarr response field {name} must be a nonnegative integer string.");
    private static bool RequiredBool(JsonElement item, string name) =>
        item.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean()
            : throw new InvalidDataException($"Lidarr response field {name} must be a boolean.");
    private static string RequiredString(JsonElement item, string name) =>
        item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()!
            : throw new InvalidDataException($"Lidarr response field {name} must be a non-empty string.");
}
