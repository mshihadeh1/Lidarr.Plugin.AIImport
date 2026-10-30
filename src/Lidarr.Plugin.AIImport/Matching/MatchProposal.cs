namespace Lidarr.Plugin.AIImport.Matching;

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

public sealed class MatchSchemaException : Exception
{
    public MatchSchemaException(string message) : base(message)
    {
    }
}
