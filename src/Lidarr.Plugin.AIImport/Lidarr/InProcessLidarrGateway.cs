using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Lidarr.Plugin.AIImport.Discovery;
using NzbDrone.Common.EnvironmentInfo;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Download.TrackedDownloads;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.MediaFiles.TrackImport.Manual;
using NzbDrone.Core.Music;
using Lidarr.Plugin.AIImport.Workflows;

namespace Lidarr.Plugin.AIImport.Lidarr;

public interface IInProcessLidarrGateway
{
    string Version { get; }
    IReadOnlyList<QueueCandidate> GetTrackedDownloads();
    IReadOnlyList<SourceFileCandidate> GetManualImports(string downloadId);
    IReadOnlyList<ReleaseCandidate> GetReleaseCandidates(int albumId);
}

public sealed class InProcessLidarrGateway : IInProcessLidarrGateway
{
    private readonly ITrackedDownloadService _trackedDownloads;
    private readonly IManualImportService _manualImports;
    private readonly IAlbumService _albums;
    private readonly IReleaseService _releases;
    private readonly ITrackService _tracks;
    private readonly IRuntimeInfo _runtimeInfo;
    private readonly IConfigFileProvider _config;

    public InProcessLidarrGateway(
        ITrackedDownloadService trackedDownloads,
        IManualImportService manualImports,
        IAlbumService albums,
        IReleaseService releases,
        ITrackService tracks,
        IRuntimeInfo runtimeInfo,
        IConfigFileProvider config,
        IAppFolderInfo appFolderInfo)
    {
        _trackedDownloads = trackedDownloads;
        _manualImports = manualImports;
        _albums = albums;
        _releases = releases;
        _tracks = tracks;
        _runtimeInfo = runtimeInfo;
        _config = config;
        new StandaloneStatePaths(appFolderInfo).ConfigureAuthorizationEnvironment();
    }

    public string Version => typeof(ITrackedDownloadService).Assembly.GetName().Version?.ToString() ?? string.Empty;

    public IReadOnlyList<QueueCandidate> GetTrackedDownloads()
    {
        _ = _runtimeInfo.Mode;
        _ = _config.Branch;
        return _trackedDownloads.GetTrackedDownloads()
            .Where(x => x.DownloadItem is not null)
            .Where(x =>
            {
                var item = x.ImportItem ?? x.DownloadItem;
                var path = item?.OutputPath.FullPath;
                return !string.IsNullOrWhiteSpace(path) && Path.IsPathFullyQualified(path) &&
                    (Directory.Exists(path) || File.Exists(path));
            })
            .Select(x =>
            {
                var item = x.ImportItem ?? x.DownloadItem;
                return new QueueCandidate(
                    StableQueueId(item.DownloadId),
                    x.RemoteAlbum?.Albums?.Select(album => (int?)album.Id).FirstOrDefault(),
                    item.Title ?? string.Empty,
                    ToState(x.State),
                    item.DownloadId ?? string.Empty);
            })
            .Where(x => !string.IsNullOrWhiteSpace(x.DownloadId))
            .OrderBy(x => x.Id)
            .ThenBy(x => x.DownloadId, StringComparer.Ordinal)
            .ToArray();
    }

    public IReadOnlyList<SourceFileCandidate> GetManualImports(string downloadId)
    {
        if (string.IsNullOrWhiteSpace(downloadId))
        {
            throw new ArgumentException("A tracked download ID is required.", nameof(downloadId));
        }

        var tracked = _trackedDownloads.Find(downloadId)
            ?? throw new InvalidDataException("The tracked download no longer exists.");
        var item = tracked.ImportItem ?? tracked.DownloadItem
            ?? throw new InvalidDataException("The tracked download has no import item.");
        var path = item.OutputPath.FullPath;
        if (!Path.IsPathFullyQualified(path))
        {
            throw new InvalidDataException("The tracked download output path is not absolute.");
        }

        var artist = tracked.RemoteAlbum?.Artist;
        var files = _manualImports.GetMediaFiles(path, downloadId, artist, FilterFilesType.None, false);
        var result = files.Select(file =>
        {
            var tags = file.Tags;
            return new SourceFileCandidate(
                OpaqueSourceId(downloadId, file.Path),
                file.Name ?? Path.GetFileName(file.Path),
                tags?.ArtistTitle ?? file.Artist?.Name ?? string.Empty,
                tags?.AlbumTitle ?? file.Album?.Title ?? string.Empty,
                tags?.Title ?? string.Empty,
                tags?.DiscNumber ?? 0,
                tags?.TrackNumbers ?? Array.Empty<int>(),
                tags is null ? 0 : checked((int)Math.Min(int.MaxValue, tags.Duration.TotalMilliseconds)),
                NullIfBlank(tags?.TrackMBId),
                file.Rejections?.Select(x => x.Reason).Where(x => !string.IsNullOrWhiteSpace(x)).ToArray() ?? Array.Empty<string>(),
                file.Artist?.Id > 0 ? file.Artist.Id : null,
                file.Album?.Id > 0 ? file.Album.Id : null,
                NullIfBlank(tags?.ArtistMBId),
                NullIfBlank(tags?.AlbumMBId),
                NullIfBlank(tags?.ReleaseMBId),
                NullIfBlank(tags?.RecordingMBId))
            {
                SourcePath = file.Path,
                Quality = JsonSerializer.SerializeToNode(file.Quality)?.AsObject()
                    ?? throw new InvalidDataException("Lidarr manual-import quality is missing."),
                IndexerFlags = file.IndexerFlags,
                DownloadId = file.DownloadId ?? downloadId,
                Size = file.Size,
                ManualImportId = file.Id
            };
        }).ToArray();

        if (result.Select(x => x.Id).Distinct(StringComparer.Ordinal).Count() != result.Length)
        {
            throw new InvalidDataException("Lidarr returned duplicate manual-import source identities.");
        }

        return result;
    }

    public IReadOnlyList<ReleaseCandidate> GetReleaseCandidates(int albumId)
    {
        if (albumId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(albumId));
        }

        var album = _albums.GetAlbum(albumId);
        if (album.Id != albumId || album.ArtistId <= 0)
        {
            throw new InvalidDataException("The Lidarr album ownership relationship is invalid.");
        }

        var artistName = album.Artist?.Value?.Name ?? string.Empty;
        var result = _releases.GetReleasesByAlbum(albumId)
            .OrderBy(x => x.Id)
            .Select(release =>
            {
                if (release.AlbumId != albumId || release.Id <= 0)
                {
                    throw new InvalidDataException("A Lidarr release does not belong to the requested album.");
                }

                var tracks = _tracks.GetTracksByRelease(release.Id).OrderBy(x => x.Id).Select(track =>
                {
                    if (track.Id <= 0 || track.AlbumReleaseId != release.Id)
                    {
                        throw new InvalidDataException("A Lidarr track does not belong to the requested release.");
                    }

                    return new TrackCandidate(
                        track.Id,
                        track.Title ?? string.Empty,
                        track.MediumNumber,
                        track.TrackNumber ?? string.Empty,
                        track.Duration,
                        NullIfBlank(track.ForeignTrackId),
                        NullIfBlank(track.ForeignRecordingId),
                        track.HasFile);
                }).ToArray();

                if (tracks.Select(x => x.Id).Distinct().Count() != tracks.Length)
                {
                    throw new InvalidDataException("Lidarr returned duplicate track IDs for a release.");
                }

                return new ReleaseCandidate(
                    albumId,
                    release.Id,
                    artistName,
                    release.Title ?? string.Empty,
                    NullIfBlank(release.ForeignReleaseId),
                    album.Title ?? string.Empty,
                    release.Disambiguation ?? string.Empty,
                    tracks,
                    album.ArtistId);
            }).ToArray();

        if (result.Select(x => x.Id).Distinct().Count() != result.Length)
        {
            throw new InvalidDataException("Lidarr returned duplicate release IDs for an album.");
        }

        return result;
    }

    private static TrackedImportState ToState(TrackedDownloadState state) => state switch
    {
        TrackedDownloadState.ImportBlocked => TrackedImportState.ImportBlocked,
        TrackedDownloadState.ImportFailed => TrackedImportState.ImportFailed,
        TrackedDownloadState.Imported => TrackedImportState.Imported,
        _ => TrackedImportState.Other
    };

    private static int StableQueueId(string downloadId)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(downloadId ?? string.Empty));
        return Math.Max(1, BitConverter.ToInt32(hash, 0) & int.MaxValue);
    }

    private static string OpaqueSourceId(string downloadId, string path)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(downloadId + "\n" + path));
        return Convert.ToHexString(hash).ToLowerInvariant()[..24];
    }

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

}
