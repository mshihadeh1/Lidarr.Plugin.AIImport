using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Lidarr.Plugin.AIImport.Execution;
using NzbDrone.Core.History;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Messaging.Commands;
using NzbDrone.Core.Music;
using NzbDrone.Core.Parser.Model;

namespace Lidarr.Plugin.AIImport.Lidarr;

public sealed class DirectLidarrReconciliationClient : ILidarrReconciliationClient
{
    private readonly IManageCommandQueue _commands;
    private readonly IHistoryService _history;
    private readonly IMediaFileService _mediaFiles;
    private readonly ITrackService _tracks;

    public DirectLidarrReconciliationClient(
        IManageCommandQueue commands,
        IHistoryService history,
        IMediaFileService mediaFiles,
        ITrackService tracks)
    {
        _commands = commands;
        _history = history;
        _mediaFiles = mediaFiles;
        _tracks = tracks;
    }

    public Task<LidarrCommandState> GetCommandAsync(int commandId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (commandId <= 0) throw new ArgumentOutOfRangeException(nameof(commandId));
        var command = _commands.Get(commandId);
        if (command.Id != commandId) throw new InvalidDataException("Lidarr command identity did not match the requested command.");
        return Task.FromResult(new LidarrCommandState(
            command.Id, command.Name ?? string.Empty, command.Status.ToString().ToLowerInvariant(),
            command.Result.ToString().ToLowerInvariant()));
    }

    public Task<IReadOnlyList<LidarrImportHistoryEvidence>> GetImportHistoryAsync(
        string downloadId,
        int albumId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(downloadId)) throw new ArgumentException("Download ID is required.", nameof(downloadId));
        if (albumId <= 0) throw new ArgumentOutOfRangeException(nameof(albumId));
        var records = _history.Find(downloadId, EntityHistoryEventType.TrackFileImported)
            .Where(x => x.AlbumId == albumId).OrderByDescending(x => x.Date).ToArray();
        if (records.Length > 100)
            throw new InvalidDataException("Lidarr reconciliation history exceeded its safety bound.");
        IReadOnlyList<LidarrImportHistoryEvidence> result = records.Select(item =>
            new LidarrImportHistoryEvidence(
                RequiredPositive(item.Id, "history ID"),
                RequiredPositive(item.ArtistId, "artist ID"),
                RequiredPositive(item.AlbumId, "album ID"),
                RequiredPositive(item.TrackId, "track ID"),
                RequiredText(item.DownloadId, "download ID"),
                new DateTimeOffset(DateTime.SpecifyKind(item.Date, DateTimeKind.Utc)),
                ParsePositive(item.Data, "fileId"),
                RequiredData(item.Data, "droppedPath"),
                RequiredData(item.Data, "importedPath"),
                ParseNonNegativeLong(item.Data, "size"),
                ParseIndexerFlags(item.Data, "indexerFlags"),
                Quality(item.Quality))).ToArray();
        return Task.FromResult(result);
    }

    public Task<IReadOnlyList<LidarrTrackFileEvidence>> GetTrackFilesAsync(
        int albumId,
        IReadOnlyList<int> trackFileIds,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (albumId <= 0) throw new ArgumentOutOfRangeException(nameof(albumId));
        var ids = ValidIds(trackFileIds, nameof(trackFileIds));
        var files = _mediaFiles.Get(ids).Where(x => x.AlbumId == albumId).Select(file =>
            new LidarrTrackFileEvidence(
                file.Id,
                RequiredPositive(file.Artist?.Value?.Id ?? 0, "track-file artist ID"),
                file.AlbumId,
                RequiredAbsolutePath(file.Path, "track-file path"),
                file.Size,
                Quality(file.Quality),
                (int)file.IndexerFlags)).ToArray();
        return Task.FromResult<IReadOnlyList<LidarrTrackFileEvidence>>(files);
    }

    public Task<IReadOnlyList<LidarrTrackBinding>> GetTracksAsync(
        IReadOnlyList<int> trackIds,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var ids = ValidIds(trackIds, nameof(trackIds));
        var tracks = _tracks.GetTracks(ids).Select(track => new LidarrTrackBinding(
            track.Id,
            RequiredPositive(track.Artist?.Value?.Id ?? 0, "track artist ID"),
            RequiredPositive(track.AlbumId, "track album ID"),
            track.TrackFileId,
            track.HasFile)).ToArray();
        return Task.FromResult<IReadOnlyList<LidarrTrackBinding>>(tracks);
    }

    private static List<int> ValidIds(IReadOnlyList<int> values, string name)
    {
        if (values.Count == 0 || values.Any(x => x <= 0) || values.Distinct().Count() != values.Count)
            throw new ArgumentException("IDs must be positive and unique.", name);
        return values.ToList();
    }

    private static JsonObject Quality(object quality) =>
        JsonSerializer.SerializeToNode(quality)?.AsObject()
        ?? throw new InvalidDataException("Lidarr quality evidence is missing.");
    private static int RequiredPositive(int value, string name) => value > 0 ? value : throw new InvalidDataException($"Lidarr {name} is invalid.");
    private static string RequiredText(string? value, string name) => !string.IsNullOrWhiteSpace(value) ? value : throw new InvalidDataException($"Lidarr {name} is missing.");
    private static string RequiredAbsolutePath(string? value, string name) =>
        !string.IsNullOrWhiteSpace(value) && Path.IsPathFullyQualified(value)
            ? value : throw new InvalidDataException($"Lidarr {name} is not absolute.");
    private static string RequiredData(IReadOnlyDictionary<string, string> data, string name) =>
        data.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value) ? value : throw new InvalidDataException($"Lidarr history {name} is missing.");
    private static int ParsePositive(IReadOnlyDictionary<string, string> data, string name) =>
        int.TryParse(RequiredData(data, name), NumberStyles.None, CultureInfo.InvariantCulture, out var value) && value > 0
            ? value : throw new InvalidDataException($"Lidarr history {name} is invalid.");
    private static long ParseNonNegativeLong(IReadOnlyDictionary<string, string> data, string name) =>
        long.TryParse(RequiredData(data, name), NumberStyles.None, CultureInfo.InvariantCulture, out var value) && value >= 0
            ? value : throw new InvalidDataException($"Lidarr history {name} is invalid.");
    private static int ParseIndexerFlags(IReadOnlyDictionary<string, string> data, string name)
    {
        var text = RequiredData(data, name);
        if (int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var numeric) && numeric is >= 0 and <= 127)
            return numeric;
        if (Enum.TryParse<IndexerFlags>(text, out var flags) && (int)flags is >= 0 and <= 127)
            return (int)flags;
        throw new InvalidDataException($"Lidarr history {name} is invalid.");
    }
}
