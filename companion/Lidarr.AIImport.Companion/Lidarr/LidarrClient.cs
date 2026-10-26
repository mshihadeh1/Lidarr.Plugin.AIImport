using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Lidarr.AIImport.Companion.Configuration;
using Lidarr.AIImport.Companion.Models;

namespace Lidarr.AIImport.Companion.Lidarr;

public interface ILidarrClient
{
    Task<LidarrSystemStatus> GetSystemStatusAsync(CancellationToken cancellationToken);
    Task<IReadOnlyList<QueueCandidate>> GetQueueAsync(CancellationToken cancellationToken);
    Task<IReadOnlyList<SourceFileCandidate>> GetManualImportsAsync(string? downloadId, string? folder, CancellationToken cancellationToken);
    Task<IReadOnlyList<ReleaseCandidate>> GetReleaseCandidatesAsync(int albumId, CancellationToken cancellationToken);
}

public sealed class LidarrClient : ILidarrClient
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
                NestedInt(item, "album", "id")));
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

    private async Task<JsonDocument> GetJsonAsync(string relativePath, CancellationToken cancellationToken)
    {
        var baseUri = _options.LidarrBaseUrl.TrimEnd('/') + "/";
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(new Uri(baseUri), relativePath));
        request.Headers.Add("X-Api-Key", _options.LidarrApiKey);
        using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        return await JsonDocument.ParseAsync(stream, DocumentOptions, cancellationToken).ConfigureAwait(false);
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
    private static bool RequiredBool(JsonElement item, string name) =>
        item.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean()
            : throw new InvalidDataException($"Lidarr response field {name} must be a boolean.");
}
