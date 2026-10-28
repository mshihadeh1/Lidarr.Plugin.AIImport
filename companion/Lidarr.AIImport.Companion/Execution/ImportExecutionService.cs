using System.Text.Json;
using Lidarr.AIImport.Companion.Approval;
using Lidarr.AIImport.Companion.Authorization;
using Lidarr.AIImport.Companion.Lidarr;

namespace Lidarr.AIImport.Companion.Execution;

public sealed record ImportExecutionStarted(int CommandId, string CommandStatus, IReadOnlyList<string> AuthorizationIds);

public sealed class ImportOutcomeIndeterminateException(string message, Exception innerException)
    : Exception(message, innerException);

public sealed class ImportExecutionService
{
    private readonly ApprovalStore _approvals;
    private readonly IApprovedPlanRevalidator _revalidator;
    private readonly ITransferAuthorizationStore _authorizations;
    private readonly ILidarrExecutionClient _lidarr;

    public ImportExecutionService(
        ApprovalStore approvals,
        IApprovedPlanRevalidator revalidator,
        ITransferAuthorizationStore authorizations,
        ILidarrExecutionClient lidarr)
    {
        _approvals = approvals;
        _revalidator = revalidator;
        _authorizations = authorizations;
        _lidarr = lidarr;
    }

    public async Task<ImportExecutionStarted> ExecuteAsync(
        string approvalId,
        string token,
        CancellationToken cancellationToken)
    {
        var authenticated = await _approvals.AuthenticateAsync(approvalId, token, cancellationToken).ConfigureAwait(false);
        ApprovedImportPlan plan;
        try
        {
            plan = authenticated.Payload.Deserialize<ApprovedImportPlan>()
                ?? throw new InvalidDataException("Approved import payload is empty.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("Approved import payload is malformed.", exception);
        }
        if (!string.Equals(plan.AnalysisId, authenticated.AnalysisId, StringComparison.Ordinal) || plan.Files.Count == 0)
            throw new InvalidDataException("Approved import payload does not match its approval record.");

        await _revalidator.RevalidateAsync(plan, cancellationToken).ConfigureAwait(false);
        await _approvals.BeginExecutionAsync(approvalId, token, plan.AnalysisId, cancellationToken).ConfigureAwait(false);
        try
        {
            var published = await _authorizations.PublishAsync(plan, authenticated.ExpiresAt, cancellationToken).ConfigureAwait(false);
            if (published.Count != plan.Files.Count)
                throw new InvalidDataException("Native authorization publication was incomplete.");
            var filesBySourceId = plan.Files.ToDictionary(file => file.SourceId, StringComparer.Ordinal);
            var bindings = published.Select(item =>
            {
                if (!filesBySourceId.TryGetValue(item.SourceId, out var file) ||
                    item.TrackId != file.TrackId ||
                    !string.Equals(item.SourcePath, file.SourcePath, StringComparison.Ordinal) ||
                    item.SourceDevice != file.Identity.Device || item.SourceInode != file.Identity.Inode)
                    throw new InvalidDataException("Published native authorization did not match its approved source file.");
                return new ApprovalAuthorizationBinding(
                    item.AuthorizationId, item.SourceId, item.TrackId, item.SourcePath,
                    item.SourceDevice, item.SourceInode);
            }).ToArray();
            if (bindings.Select(binding => binding.SourceId).Distinct(StringComparer.Ordinal).Count() != plan.Files.Count)
                throw new InvalidDataException("Native authorization publication did not bind every approved source exactly once.");
            await _approvals.RecordAuthorizationBindingsAsync(approvalId, bindings, cancellationToken).ConfigureAwait(false);
            var authorizationIds = bindings.Select(binding => binding.AuthorizationId).ToArray();
            var command = await _lidarr.SubmitManualImportAsync(plan, cancellationToken).ConfigureAwait(false);
            await _approvals.RecordCommandAsync(
                approvalId,
                command.CommandId,
                command.Status,
                CancellationToken.None).ConfigureAwait(false);
            return new ImportExecutionStarted(command.CommandId, command.Status, authorizationIds);
        }
        catch (Exception exception)
        {
            try
            {
                await _approvals.CompleteAsync(
                    approvalId,
                    ApprovalState.Indeterminate,
                    "Execution crossed the durable start boundary but did not reach verified reconciliation; automatic retry is forbidden.",
                    CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception completionException)
            {
                throw new AggregateException(
                    "Import outcome is indeterminate and the durable approval state could not be updated.",
                    exception,
                    completionException);
            }
            throw new ImportOutcomeIndeterminateException(
                "Import outcome is indeterminate; reconcile authorization, filesystem, Lidarr track-file, and history state before any new approval.",
                exception);
        }
    }
}
