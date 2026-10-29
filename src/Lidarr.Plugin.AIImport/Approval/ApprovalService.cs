using System.Text.Json;

namespace Lidarr.Plugin.AIImport.Approval;

public sealed class ApprovalService : IApprovedPlanRevalidator
{
    private readonly IApprovedPlanProvider _plans;
    private readonly ApprovalStore _store;

    public ApprovalService(IApprovedPlanProvider plans, ApprovalStore store)
    {
        _plans = plans;
        _store = store;
    }

    public async Task<CreatedApproval> ApproveAsync(string analysisId, TimeSpan lifetime, CancellationToken cancellationToken)
    {
        var plan = await _plans.BuildCurrentAsync(analysisId, cancellationToken).ConfigureAwait(false);
        Validate(plan, analysisId);
        return await _store.CreateAsync(plan.AnalysisId, JsonSerializer.SerializeToElement(plan), lifetime, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task RevalidateAsync(ApprovedImportPlan expected, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(expected);
        var current = await _plans.BuildCurrentAsync(expected.AnalysisId, cancellationToken).ConfigureAwait(false);
        Validate(current, expected.AnalysisId);
        if (!string.Equals(JsonSerializer.Serialize(expected), JsonSerializer.Serialize(current), StringComparison.Ordinal))
            throw new StaleApprovalException("The approved import plan no longer matches current Lidarr, safety, or source state.");
    }

    private static void Validate(ApprovedImportPlan plan, string analysisId)
    {
        if (!string.Equals(plan.AnalysisId, analysisId, StringComparison.Ordinal) || plan.ArtistId <= 0 ||
            plan.AlbumId <= 0 || plan.ReleaseId <= 0 || plan.Files.Count == 0 ||
            string.IsNullOrWhiteSpace(plan.SafetyFingerprint) || !Path.IsPathFullyQualified(plan.DestinationRoot) ||
            plan.Files.Any(file => file.TrackId <= 0 || file.ManualImportId <= 0 || file.Size < 0 ||
                                   file.Identity.Size != file.Size || !Path.IsPathFullyQualified(file.SourcePath)) ||
            plan.Files.Select(file => file.SourceId).Distinct(StringComparer.Ordinal).Count() != plan.Files.Count ||
            plan.Files.Select(file => file.TrackId).Distinct().Count() != plan.Files.Count)
            throw new InvalidDataException("The current import plan is incomplete or not one-to-one.");
    }
}