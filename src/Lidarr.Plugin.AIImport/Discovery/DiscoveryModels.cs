using Lidarr.Plugin.AIImport.Matching;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Lidarr.Plugin.AIImport.Discovery;

public enum TrackedImportState
{
    Other,
    ImportBlocked,
    ImportFailed,
    Imported
}

public sealed record QueueCandidate(int Id, int? AlbumId, string Title, TrackedImportState State, string DownloadId);

public sealed record SourceFileCandidate(
    string Id,
    string Name,
    string Artist,
    string Album,
    string Title,
    int DiscNumber,
    IReadOnlyList<int> TrackNumbers,
    int DurationMs,
    string? TrackMusicBrainzId,
    IReadOnlyList<string> Rejections,
    int? ArtistId = null,
    int? AlbumId = null,
    string? ArtistMusicBrainzId = null,
    string? AlbumMusicBrainzId = null,
    string? ReleaseMusicBrainzId = null,
    string? RecordingMusicBrainzId = null)
{
    [JsonIgnore] public string? SourcePath { get; init; }
    [JsonIgnore] public JsonObject? Quality { get; init; }
    [JsonIgnore] public int IndexerFlags { get; init; }
    [JsonIgnore] public string? DownloadId { get; init; }
    [JsonIgnore] public long Size { get; init; }
    [JsonIgnore] public int ManualImportId { get; init; }
}

public sealed record TrackCandidate(
    int Id,
    string Title,
    int DiscNumber,
    string TrackNumber,
    int DurationMs,
    string? ForeignTrackId,
    string? ForeignRecordingId,
    bool HasFile);

public sealed record ReleaseCandidate(
    int AlbumId,
    int Id,
    string Artist,
    string Title,
    string? ForeignReleaseId,
    string AlbumTitle,
    string Disambiguation,
    IReadOnlyList<TrackCandidate> Tracks,
    int? ArtistId = null);

public sealed record SourceFileDto(
    string Id,
    string Name,
    string Artist,
    string Album,
    string Title,
    int DiscNumber,
    IReadOnlyList<int> TrackNumbers,
    int DurationMs);

public sealed record ReleaseCandidateDto(
    int AlbumId,
    int ReleaseId,
    int? ArtistId,
    string Artist,
    string Title,
    string? ForeignReleaseId);

public sealed record AiImportAnalysisDto(
    string AnalysisId,
    string DownloadId,
    string Title,
    MatchProposal Proposal,
    IReadOnlyList<SourceFileDto> Files,
    IReadOnlyList<ReleaseCandidateDto> Candidates,
    bool UsedLlm);
