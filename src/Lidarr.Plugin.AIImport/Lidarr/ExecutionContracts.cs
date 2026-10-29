using Lidarr.Plugin.AIImport.Approval;

namespace Lidarr.Plugin.AIImport.Lidarr;

public sealed record LidarrCommandReceipt(int CommandId, string Status);

public interface ILidarrExecutionClient
{
    Task<string> GetArtistPathAsync(int artistId, CancellationToken cancellationToken);
    Task RevalidateManualImportAsync(ApprovedImportPlan plan, CancellationToken cancellationToken);
    Task<LidarrCommandReceipt> SubmitManualImportAsync(ApprovedImportPlan plan, CancellationToken cancellationToken);
}