using Lidarr.Plugin.AIImport.Approval;
using Lidarr.Plugin.AIImport.Execution;
using Lidarr.Plugin.AIImport.Diagnostics;

namespace Lidarr.Plugin.AIImport.Workflows;

public interface IStandaloneAiImportWorkflow
{
    LibraryAdditionCreatedDto AddLibraryEntities(
        string analysisId, int rootFolderId, int qualityProfileId, int metadataProfileId, string confirmation);
    ManualMappingCreatedDto CreateManualMapping(string analysisId, int releaseId, string mappingsJson, string confirmation);
    ApprovalCreatedDto Approve(string analysisId);
    ImportExecutionStarted Execute(string approvalId, string confirmation);
    ImportReconciliationResult Reconcile(string approvalId);
    ApprovalStatusDto Status(string approvalId);
}

public sealed record ApprovalCreatedDto(string ApprovalId, DateTimeOffset ExpiresAt);

public sealed record ApprovalStatusDto(
    string ApprovalId,
    string AnalysisId,
    ApprovalState State,
    DateTimeOffset ExpiresAt,
    int? CommandId,
    string? CommandStatus,
    string? Outcome);

public sealed class StandaloneAiImportWorkflow : IStandaloneAiImportWorkflow
{
    private readonly ApprovalService _approval;
    private readonly ApprovalStore _store;
    private readonly ImportExecutionService _execution;
    private readonly ImportReconciliationCoordinator _reconciliation;
    private readonly ManualMappingService _manualMapping;
    private readonly LibraryAdditionService _libraryAddition;


    public StandaloneAiImportWorkflow(
        ApprovalService approval,
        ApprovalStore store,
        ImportExecutionService execution,
        ImportReconciliationCoordinator reconciliation,
        ManualMappingService manualMapping,
        LibraryAdditionService libraryAddition)
    {
        _approval = approval;
        _store = store;
        _execution = execution;
        _reconciliation = reconciliation;
        _manualMapping = manualMapping;
        _libraryAddition = libraryAddition;
        _store.RecoverInterruptedExecutionsAsync(CancellationToken.None).GetAwaiter().GetResult();
    }

    public ManualMappingCreatedDto CreateManualMapping(string analysisId, int releaseId, string mappingsJson, string confirmation) =>
        _manualMapping.Create(Required(analysisId, nameof(analysisId)), releaseId,
            Required(mappingsJson, nameof(mappingsJson)), Required(confirmation, nameof(confirmation)));

    public LibraryAdditionCreatedDto AddLibraryEntities(
        string analysisId, int rootFolderId, int qualityProfileId, int metadataProfileId, string confirmation) =>
        _libraryAddition.Add(Required(analysisId, nameof(analysisId)), rootFolderId,
            qualityProfileId, metadataProfileId, Required(confirmation, nameof(confirmation)));

    public ApprovalCreatedDto Approve(string analysisId)
    {
        var created = _approval.ApproveAsync(Required(analysisId, nameof(analysisId)), TimeSpan.FromMinutes(15), CancellationToken.None)
            .GetAwaiter().GetResult();
        return new ApprovalCreatedDto(created.ApprovalId, created.ExpiresAt);
    }

    public ImportExecutionStarted Execute(string approvalId, string confirmation)
    {
        if (!string.Equals(confirmation, "IMPORT", StringComparison.Ordinal))
            throw new ArgumentException("The exact execution confirmation is required.", nameof(confirmation));
        return _execution.ExecuteAsync(Required(approvalId, nameof(approvalId)), string.Empty,
                CancellationToken.None, trustedInProcess: true)
            .GetAwaiter().GetResult();
    }

    public ImportReconciliationResult Reconcile(string approvalId)
    {
        var required = Required(approvalId, nameof(approvalId));
        AiImportWorkflowDiagnostics.Info("reconcile", "started", required);
        var result = _reconciliation.ReconcileAsync(required, string.Empty, CancellationToken.None, trustedInProcess: true)
            .GetAwaiter().GetResult();
        AiImportWorkflowDiagnostics.Info("reconcile", "completed", required, $"status={result.Status}");
        return result;
    }

    public ApprovalStatusDto Status(string approvalId)
    {
        var record = _store.AuthenticateForStatusTrustedAsync(
            Required(approvalId, nameof(approvalId)), CancellationToken.None)
            .GetAwaiter().GetResult();
        return new ApprovalStatusDto(record.ApprovalId, record.AnalysisId, record.State, record.ExpiresAt,
            record.CommandId, record.CommandStatus, record.Outcome);
    }

    private static string Required(string? value, string name) =>
        !string.IsNullOrWhiteSpace(value) ? value : throw new ArgumentException($"{name} is required.", name);
}