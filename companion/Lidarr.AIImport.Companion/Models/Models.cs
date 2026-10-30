using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Lidarr.AIImport.Companion.Models;

public sealed record LidarrSystemStatus(string Version, string PackageVersion, string Branch);

public sealed record QueueCandidate(
    int Id,
    int? AlbumId,
    string Title,
    string Status,
    string? TrackedDownloadState,
    string DownloadId,
    string? OutputPath,
    IReadOnlyList<string> StatusMessages);

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
    string? RecordingMusicBrainzId = null,
    [property: JsonIgnore] string? SourcePath = null,
    [property: JsonIgnore] JsonObject? Quality = null,
    [property: JsonIgnore] int IndexerFlags = 0,
    [property: JsonIgnore] string? DownloadId = null,
    [property: JsonIgnore] long Size = 0,
    [property: JsonIgnore] int ManualImportId = 0);

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

public enum ProposalStatus
{
    Proposed,
    ManualReview
}

public sealed record TrackProposal(string SourceFileId, int TrackId, decimal Confidence);

public sealed record MatchProposal(
    int SchemaVersion,
    ProposalStatus Status,
    int? ArtistId,
    int? AlbumId,
    int? ReleaseId,
    decimal Confidence,
    string Explanation,
    IReadOnlyList<TrackProposal> Tracks);

public sealed record AnalysisResult(
    string AnalysisId,
    string DownloadId,
    string Title,
    MatchProposal Proposal,
    IReadOnlyList<SourceFileCandidate> Files,
    IReadOnlyList<ReleaseCandidate> Candidates,
    bool UsedLlm,
    DateTimeOffset CreatedAt,
    MissingAlbumProposal? MissingAlbum = null);

public sealed record MissingAlbumProposal(
    Guid ArtistMusicBrainzId,
    Guid ReleaseGroupMusicBrainzId,
    Guid ReleaseMusicBrainzId,
    string Artist,
    string Album);
