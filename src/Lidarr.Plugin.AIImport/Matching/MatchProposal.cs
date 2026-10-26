namespace Lidarr.Plugin.AIImport.Matching;

public enum MatchStatus
{
    Proposed,
    ManualReview
}

public sealed record TrackMatchProposal(string SourceFileId, int TrackId, decimal Confidence);

public sealed record MatchProposal(
    int SchemaVersion,
    MatchStatus Status,
    int? ArtistId,
    int? AlbumId,
    int? ReleaseId,
    decimal Confidence,
    string Explanation,
    IReadOnlyList<TrackMatchProposal> Tracks);

public sealed class MatchSchemaException : Exception
{
    public MatchSchemaException(string message) : base(message)
    {
    }
}
