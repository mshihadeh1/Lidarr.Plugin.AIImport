using System.Text.Json.Nodes;
using Lidarr.Plugin.AIImport.Filesystem;

namespace Lidarr.Plugin.AIImport.Approval;

public sealed record ApprovedImportFile(
    string SourceId,
    int ManualImportId,
    string SourcePath,
    int TrackId,
    JsonObject Quality,
    int IndexerFlags,
    string DownloadId,
    long Size,
    FileIdentity Identity);

public sealed record ApprovedImportPlan(
    string AnalysisId,
    string DownloadId,
    int ArtistId,
    int AlbumId,
    int ReleaseId,
    string SafetyFingerprint,
    IReadOnlyList<ApprovedImportFile> Files,
    string DestinationRoot = "");

public interface IApprovedPlanProvider
{
    Task<ApprovedImportPlan> BuildCurrentAsync(string analysisId, CancellationToken cancellationToken);
}

public interface IApprovedPlanRevalidator
{
    Task RevalidateAsync(ApprovedImportPlan expected, CancellationToken cancellationToken);
}