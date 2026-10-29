using System.Text.Json;
using Lidarr.Plugin.AIImport.Approval;

namespace Lidarr.Plugin.AIImport.Execution;

public sealed class ImportReconciliationCoordinator
{
    private readonly ApprovalStore _approvals;
    private readonly IImportReconciliationService _reconciliation;

    public ImportReconciliationCoordinator(
        ApprovalStore approvals,
        IImportReconciliationService reconciliation)
    {
        _approvals = approvals;
        _reconciliation = reconciliation;
    }

    public async Task<ImportReconciliationResult> ReconcileAsync(
        string approvalId,
        string token,
        CancellationToken cancellationToken,
        bool trustedInProcess = false)
    {
        var record = trustedInProcess
            ? await _approvals.AuthenticateForReconciliationTrustedAsync(approvalId, cancellationToken).ConfigureAwait(false)
            : await _approvals.AuthenticateForReconciliationAsync(approvalId, token, cancellationToken).ConfigureAwait(false);
        ApprovedImportPlan plan;
        try
        {
            plan = record.Payload.Deserialize<ApprovedImportPlan>()
                ?? throw new InvalidDataException("Approved import payload is empty.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("Approved import payload is malformed.", exception);
        }
        if (!string.Equals(plan.AnalysisId, record.AnalysisId, StringComparison.Ordinal) || plan.Files.Count == 0)
            throw new InvalidDataException("Approved import payload does not match its approval record.");
        if (record.AuthorizationBindings is null || record.AuthorizationBindings.Count != plan.Files.Count ||
            record.ExecutionStartedAt is null)
            throw new ImportReconciliationException(
                "Approval has not durably recorded all material required for reconciliation.");

        try
        {
            var result = await _reconciliation.ReconcileAsync(
                plan,
                record.AuthorizationBindings,
                record.CommandId,
                record.ExecutionStartedAt.Value,
                cancellationToken).ConfigureAwait(false);
            if (result.Status == ImportReconciliationStatus.Succeeded)
            {
                await _approvals.CompleteVerifiedReconciliationAsync(
                    approvalId,
                    result.Message,
                    CancellationToken.None).ConfigureAwait(false);
            }
            return result;
        }
        catch (ImportReconciliationException)
        {
            if (record.State == ApprovalState.Executing)
            {
                await _approvals.CompleteAsync(
                    approvalId,
                    ApprovalState.Indeterminate,
                    "Reconciliation found incomplete or contradictory external state; automatic retry is forbidden.",
                    CancellationToken.None).ConfigureAwait(false);
            }
            throw;
        }
    }
}
