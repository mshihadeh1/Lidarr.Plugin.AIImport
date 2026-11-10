using System.Collections.Concurrent;
using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using Lidarr.Plugin.AIImport.Discovery;

namespace Lidarr.Plugin.AIImport.MusicBrainz;

public sealed record MusicBrainzTrackCandidate(
    string RecordingMusicBrainzId,
    string Title,
    int DiscNumber,
    int TrackNumber,
    int DurationMs);

public sealed record MusicBrainzReleaseCandidate(
    string ArtistMusicBrainzId,
    string ReleaseGroupMusicBrainzId,
    string ReleaseMusicBrainzId,
    string Artist,
    string Title,
    string? Date,
    string? Country,
    string? Disambiguation,
    int SearchScore,
    IReadOnlyList<MusicBrainzTrackCandidate> Tracks);

public interface IMusicBrainzReleaseClient
{
    Task<IReadOnlyList<MusicBrainzReleaseCandidate>> SearchAsync(
        IReadOnlyList<SourceFileCandidate> files,
        int limit,
        CancellationToken cancellationToken);

    Task<MusicBrainzReleaseCandidate?> GetAsync(Guid releaseId, CancellationToken cancellationToken);
}

public sealed class MusicBrainzReleaseClient : IMusicBrainzReleaseClient
{
    private const int MaximumResponseBytes = 2 * 1024 * 1024;
    private const int SearchWindow = 10;
    private static readonly Uri ApiRoot = new("https://musicbrainz.org/ws/2/");
    private static readonly SemaphoreSlim RateGate = new(1, 1);
    private static readonly ConcurrentDictionary<string, CacheEntry> Cache = new(StringComparer.Ordinal);
    private static DateTimeOffset _lastRequest = DateTimeOffset.MinValue;

    private readonly HttpClient _httpClient;
    private readonly TimeProvider _timeProvider;

    public MusicBrainzReleaseClient() : this(new HttpClient(), TimeProvider.System)
    {
    }

    internal MusicBrainzReleaseClient(HttpClient httpClient, TimeProvider timeProvider)
    {
        _httpClient = httpClient;
        _timeProvider = timeProvider;
        _httpClient.Timeout = TimeSpan.FromSeconds(30);
        if (!_httpClient.DefaultRequestHeaders.UserAgent.Any())
        {
            _httpClient.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Lidarr.Plugin.AIImport", "2.0"));
            _httpClient.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("(https://github.com/mshihadeh1/Lidarr.Plugin.AIImport)"));
        }
        _httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    public async Task<IReadOnlyList<MusicBrainzReleaseCandidate>> SearchAsync(
        IReadOnlyList<SourceFileCandidate> files,
        int limit,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(files);
        if (limit is < 1 or > 10) throw new ArgumentOutOfRangeException(nameof(limit));
        var artist = Consensus(files.Select(x => x.Artist));
        var album = Consensus(files.Select(x => x.Album));
        if (artist is null || album is null) return [];

        var query = $"artist:\"{EscapeQuery(artist)}\" AND release:\"{EscapeQuery(album)}\"";
        var uri = new Uri(ApiRoot, $"release/?query={Uri.EscapeDataString(query)}&fmt=json&limit={SearchWindow.ToString(CultureInfo.InvariantCulture)}");
        using var document = await GetJsonAsync(uri, cancellationToken).ConfigureAwait(false);
        if (!document.RootElement.TryGetProperty("releases", out var releases) || releases.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("MusicBrainz release search returned no releases array.");
        if (!document.RootElement.TryGetProperty("count", out var countElement) ||
            !countElement.TryGetInt32(out var totalCount) || totalCount < 0)
            throw new InvalidDataException("MusicBrainz release search returned no valid result count.");
        // Search fallback cannot safely pick an edition when MusicBrainz has candidates
        // outside the bounded window. Exact tag MBIDs remain the path for large edition sets.
        if (totalCount > SearchWindow) return [];

        var ids = releases.EnumerateArray()
            .Where(item => SearchTrackCount(item) == files.Count)
            .Select(item => new
            {
                Id = RequiredGuid(item, "id"),
                Score = item.TryGetProperty("score", out var score) && score.TryGetInt32(out var value) ? value : 0
            })
            .Where(x => x.Id != Guid.Empty)
            .GroupBy(x => x.Id)
            .Select(x => x.OrderByDescending(y => y.Score).First())
            .OrderByDescending(x => x.Score)
            .ThenBy(x => x.Id)
            .ToArray();
        // A configured lookup cap must never hide structurally plausible editions from
        // the model. Refuse rather than truncate an ambiguous candidate set.
        if (ids.Length > limit) return [];

        var result = new List<MusicBrainzReleaseCandidate>(ids.Length);
        foreach (var candidate in ids)
        {
            var release = await GetAsync(candidate.Id, cancellationToken).ConfigureAwait(false);
            if (release is not null) result.Add(release with { SearchScore = candidate.Score });
        }
        return result;
    }

    public async Task<MusicBrainzReleaseCandidate?> GetAsync(Guid releaseId, CancellationToken cancellationToken)
    {
        if (releaseId == Guid.Empty) throw new ArgumentOutOfRangeException(nameof(releaseId));
        var uri = new Uri(ApiRoot,
            $"release/{releaseId:D}?inc=artist-credits%2Brelease-groups%2Brecordings&fmt=json");
        using var document = await GetJsonAsync(uri, cancellationToken).ConfigureAwait(false);
        var root = document.RootElement;
        var resolvedId = RequiredGuid(root, "id");
        if (resolvedId != releaseId) throw new InvalidDataException("MusicBrainz returned a different release identity.");
        var releaseGroupId = root.TryGetProperty("release-group", out var releaseGroup)
            ? RequiredGuid(releaseGroup, "id")
            : Guid.Empty;
        var (artistId, artist) = ReadArtistCredit(root);
        var title = RequiredString(root, "title");
        if (releaseGroupId == Guid.Empty || artistId == Guid.Empty) return null;

        var tracks = new List<MusicBrainzTrackCandidate>();
        if (root.TryGetProperty("media", out var media) && media.ValueKind == JsonValueKind.Array)
        {
            var defaultDisc = 0;
            foreach (var medium in media.EnumerateArray())
            {
                defaultDisc++;
                var disc = medium.TryGetProperty("position", out var discElement) && discElement.TryGetInt32(out var discValue)
                    ? discValue
                    : defaultDisc;
                if (!medium.TryGetProperty("tracks", out var mediumTracks) || mediumTracks.ValueKind != JsonValueKind.Array) continue;
                var defaultTrack = 0;
                foreach (var track in mediumTracks.EnumerateArray())
                {
                    defaultTrack++;
                    if (!track.TryGetProperty("recording", out var recording)) continue;
                    var recordingId = RequiredGuid(recording, "id");
                    if (recordingId == Guid.Empty) continue;
                    var position = track.TryGetProperty("position", out var positionElement) && positionElement.TryGetInt32(out var positionValue)
                        ? positionValue
                        : defaultTrack;
                    var duration = track.TryGetProperty("length", out var length) && length.TryGetInt32(out var durationValue)
                        ? Math.Max(0, durationValue)
                        : 0;
                    var trackTitle = track.TryGetProperty("title", out var trackTitleElement) && trackTitleElement.ValueKind == JsonValueKind.String
                        ? trackTitleElement.GetString()
                        : null;
                    tracks.Add(new MusicBrainzTrackCandidate(
                        recordingId.ToString("D"),
                        !string.IsNullOrWhiteSpace(trackTitle) ? trackTitle! : RequiredString(recording, "title"),
                        disc,
                        position,
                        duration));
                }
            }
        }
        if (tracks.Count == 0) return null;

        return new MusicBrainzReleaseCandidate(
            artistId.ToString("D"),
            releaseGroupId.ToString("D"),
            releaseId.ToString("D"),
            artist,
            title,
            OptionalString(root, "date"),
            OptionalString(root, "country"),
            OptionalString(root, "disambiguation"),
            0,
            tracks);
    }

    private async Task<JsonDocument> GetJsonAsync(Uri uri, CancellationToken cancellationToken)
    {
        var key = uri.AbsoluteUri;
        if (Cache.TryGetValue(key, out var cached) && cached.ExpiresAt > _timeProvider.GetUtcNow())
            return JsonDocument.Parse(cached.Payload);

        await RateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            for (var attempt = 1; attempt <= 3; attempt++)
            {
                var delay = TimeSpan.FromSeconds(1) - (_timeProvider.GetUtcNow() - _lastRequest);
                if (delay > TimeSpan.Zero) await Task.Delay(delay, _timeProvider, cancellationToken).ConfigureAwait(false);
                using var response = await _httpClient.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
                _lastRequest = _timeProvider.GetUtcNow();
                if (IsTransient(response.StatusCode) && attempt < 3)
                {
                    await Task.Delay(GetRetryDelay(response), _timeProvider, cancellationToken).ConfigureAwait(false);
                    continue;
                }
                response.EnsureSuccessStatusCode();
                await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                using var memory = new MemoryStream();
                var buffer = new byte[16 * 1024];
                while (true)
                {
                    var read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                    if (read == 0) break;
                    if (memory.Length + read > MaximumResponseBytes)
                        throw new InvalidDataException("MusicBrainz response exceeded 2 MiB.");
                    memory.Write(buffer, 0, read);
                }
                var payload = memory.ToArray();
                using var validated = JsonDocument.Parse(payload);
                Cache[key] = new CacheEntry(payload, _timeProvider.GetUtcNow().AddHours(24));
                return JsonDocument.Parse(payload);
            }
            throw new HttpRequestException("MusicBrainz request exhausted its bounded retry budget.");
        }
        finally
        {
            RateGate.Release();
        }
    }

    private static bool IsTransient(System.Net.HttpStatusCode statusCode) =>
        statusCode is System.Net.HttpStatusCode.TooManyRequests or
            System.Net.HttpStatusCode.BadGateway or
            System.Net.HttpStatusCode.ServiceUnavailable or
            System.Net.HttpStatusCode.GatewayTimeout;

    private static TimeSpan GetRetryDelay(HttpResponseMessage response)
    {
        var delay = response.Headers.RetryAfter?.Delta;
        if (delay is null && response.Headers.RetryAfter?.Date is { } retryAt)
            delay = retryAt - DateTimeOffset.UtcNow;
        if (delay is null || delay <= TimeSpan.Zero) delay = TimeSpan.FromSeconds(2);
        return delay > TimeSpan.FromSeconds(30) ? TimeSpan.FromSeconds(30) : delay.Value;
    }

    private static (Guid ArtistId, string Artist) ReadArtistCredit(JsonElement root)
    {
        if (!root.TryGetProperty("artist-credit", out var credits) || credits.ValueKind != JsonValueKind.Array)
            return (Guid.Empty, string.Empty);
        foreach (var credit in credits.EnumerateArray())
        {
            if (!credit.TryGetProperty("artist", out var artist)) continue;
            var id = RequiredGuid(artist, "id");
            if (id != Guid.Empty) return (id, RequiredString(artist, "name"));
        }
        return (Guid.Empty, string.Empty);
    }

    private static string? Consensus(IEnumerable<string> values)
    {
        var grouped = values.Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x.Trim())
            .GroupBy(x => x, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(x => x.Count())
            .ThenBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return grouped.Length == 0 || grouped.Length > 1 && grouped[0].Count() == grouped[1].Count()
            ? null
            : grouped[0].Key;
    }

    private static string EscapeQuery(string value) => value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal);

    private static int SearchTrackCount(JsonElement release)
    {
        if (!release.TryGetProperty("media", out var media) || media.ValueKind != JsonValueKind.Array) return 0;
        var total = 0;
        foreach (var medium in media.EnumerateArray())
        {
            if (!medium.TryGetProperty("track-count", out var count) || !count.TryGetInt32(out var value) || value < 0)
                return 0;
            total = checked(total + value);
        }
        return total;
    }

    private static Guid RequiredGuid(JsonElement element, string property) =>
        Guid.TryParse(RequiredString(element, property), out var value) ? value : Guid.Empty;

    private static string RequiredString(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()!
            : throw new InvalidDataException($"MusicBrainz response omitted {property}.");

    private static string? OptionalString(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private sealed record CacheEntry(byte[] Payload, DateTimeOffset ExpiresAt);
}
