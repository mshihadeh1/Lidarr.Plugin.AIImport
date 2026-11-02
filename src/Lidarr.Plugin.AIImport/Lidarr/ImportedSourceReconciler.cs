using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Lidarr.Plugin.AIImport.Filesystem;
using NzbDrone.Core.History;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.MediaFiles.TrackImport.Manual;
using NzbDrone.Core.Music;
using NzbDrone.Core.Parser.Model;

namespace Lidarr.Plugin.AIImport.Lidarr;

internal enum ExistingImportClassification
{
    Pending,
    AlreadyImported,
    Indeterminate
}

internal sealed class ImportedSourceReconciler
{
    private readonly IHistoryService _history;
    private readonly IMediaFileService _mediaFiles;
    private readonly ITrackService _tracks;
    private readonly IFileIdentityProvider _identities;

    public ImportedSourceReconciler(
        IHistoryService history,
        IMediaFileService mediaFiles,
        ITrackService tracks,
        IFileIdentityProvider identities)
    {
        _history = history;
        _mediaFiles = mediaFiles;
        _tracks = tracks;
        _identities = identities;
    }

    public ExistingImportClassification Classify(ManualImportItem source, string downloadId)
    {
        var historyItems = _history.Find(downloadId, EntityHistoryEventType.TrackFileImported).ToArray();
        if (historyItems.Length > 100) return ExistingImportClassification.Indeterminate;
        var matches = historyItems
            .Where(item => item.Data.TryGetValue("droppedPath", out var path) &&
                string.Equals(path, source.Path, StringComparison.Ordinal))
            .ToArray();
        if (matches.Length == 0) return ExistingImportClassification.Pending;
        if (matches.Length != 1) return ExistingImportClassification.Indeterminate;

        try
        {
            var history = matches[0];
            var fileId = ParsePositive(history.Data, "fileId");
            var importedPath = RequiredAbsolute(history.Data, "importedPath");
            if (history.ArtistId <= 0 || history.AlbumId <= 0 || history.TrackId <= 0 ||
                !string.Equals(history.DownloadId, downloadId, StringComparison.Ordinal) ||
                ParseLong(history.Data, "size") != source.Size ||
                ParseFlags(history.Data, "indexerFlags") != source.IndexerFlags ||
                !JsonNode.DeepEquals(ToJson(history.Quality), ToJson(source.Quality)))
                return ExistingImportClassification.Indeterminate;

            if (!CurrentStateMatches(history, source, fileId, importedPath))
                return ExistingImportClassification.Indeterminate;

            var sourceIdentityBefore = _identities.InspectAsync(source.Path, CancellationToken.None).GetAwaiter().GetResult();
            var destinationIdentity = _identities.InspectAsync(importedPath, CancellationToken.None).GetAwaiter().GetResult();
            if (!CurrentStateMatches(history, source, fileId, importedPath))
                return ExistingImportClassification.Indeterminate;
            var sourceIdentityAfter = _identities.InspectAsync(source.Path, CancellationToken.None).GetAwaiter().GetResult();
            return sourceIdentityBefore == sourceIdentityAfter &&
                sourceIdentityAfter.Device == destinationIdentity.Device &&
                sourceIdentityAfter.Inode == destinationIdentity.Inode && sourceIdentityAfter.Size == source.Size
                ? ExistingImportClassification.AlreadyImported
                : ExistingImportClassification.Indeterminate;
        }
        catch (Exception exception) when (exception is ArgumentException or ApplicationException or InvalidDataException or IOException or UnauthorizedAccessException or InvalidOperationException or JsonException)
        {
            return ExistingImportClassification.Indeterminate;
        }
    }

    private bool CurrentStateMatches(EntityHistory history, ManualImportItem source, int fileId, string importedPath)
    {
        var files = _mediaFiles.Get([fileId]).Where(file => file.Id == fileId).ToArray();
        if (files.Length != 1) return false;
        var file = files[0];
        if (file.Artist?.Value?.Id != history.ArtistId || file.AlbumId != history.AlbumId ||
            !string.Equals(file.Path, importedPath, StringComparison.Ordinal) ||
            file.Size != source.Size || (int)file.IndexerFlags != source.IndexerFlags ||
            !JsonNode.DeepEquals(ToJson(file.Quality), ToJson(source.Quality)))
            return false;

        var tracks = _tracks.GetTracks([history.TrackId]).Where(track => track.Id == history.TrackId).ToArray();
        if (tracks.Length != 1) return false;
        var track = tracks[0];
        return track.HasFile && track.TrackFileId == fileId && track.AlbumId == history.AlbumId &&
            track.Artist?.Value?.Id == history.ArtistId;
    }

    private static JsonObject ToJson(object value) =>
        JsonSerializer.SerializeToNode(value)?.AsObject() ?? throw new InvalidDataException("Quality evidence is missing.");
    private static string Required(IReadOnlyDictionary<string, string> data, string name) =>
        data.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value : throw new InvalidDataException($"History {name} is missing.");
    private static string RequiredAbsolute(IReadOnlyDictionary<string, string> data, string name)
    {
        var value = Required(data, name);
        return Path.IsPathFullyQualified(value) ? value : throw new InvalidDataException($"History {name} is invalid.");
    }
    private static int ParsePositive(IReadOnlyDictionary<string, string> data, string name) =>
        int.TryParse(Required(data, name), NumberStyles.None, CultureInfo.InvariantCulture, out var value) && value > 0
            ? value : throw new InvalidDataException($"History {name} is invalid.");
    private static long ParseLong(IReadOnlyDictionary<string, string> data, string name) =>
        long.TryParse(Required(data, name), NumberStyles.None, CultureInfo.InvariantCulture, out var value) && value >= 0
            ? value : throw new InvalidDataException($"History {name} is invalid.");
    private static int ParseFlags(IReadOnlyDictionary<string, string> data, string name)
    {
        var text = Required(data, name);
        if (int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var numeric)) return numeric;
        if (Enum.TryParse<IndexerFlags>(text, out var flags)) return (int)flags;
        throw new InvalidDataException($"History {name} is invalid.");
    }
}
