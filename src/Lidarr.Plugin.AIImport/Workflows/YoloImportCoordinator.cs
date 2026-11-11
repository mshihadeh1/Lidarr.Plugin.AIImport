using Lidarr.Plugin.AIImport.Approval;
using Lidarr.Plugin.AIImport.Discovery;
using Lidarr.Plugin.AIImport.Execution;
using Lidarr.Plugin.AIImport.Matching;

namespace Lidarr.Plugin.AIImport.Workflows;

public sealed record YoloImportResult(
    string AnalysisId,
    string Status,
    string Message,
    string? ApprovalId = null,
    int? CommandId = null);

public interface IYoloImportCoordinator
{
    IReadOnlyList<YoloImportResult> Process(IReadOnlyList<AiImportAnalysisDto> analyses);
}

public sealed class YoloImportCoordinator : IYoloImportCoordinator
{
    private static readonly TimeSpan ApprovalLifetime = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan ReconciliationTimeout = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);

    private readonly ApprovalService _approval;
    private readonly ImportExecutionService _execution;
    private readonly ImportReconciliationCoordinator _reconciliation;
    private readonly TimeProvider _timeProvider;

    public YoloImportCoordinator(
        ApprovalService approval,
        ImportExecutionService execution,
        ImportReconciliationCoordinator reconciliation)
        : this(approval, execution, reconciliation, TimeProvider.System)
    {
    }

    internal YoloImportCoordinator(
        ApprovalService approval,
        ImportExecutionService execution,
        ImportReconciliationCoordinator reconciliation,
        TimeProvider timeProvider)
    {
        _approval = approval;
        _execution = execution;
        _reconciliation = reconciliation;
        _timeProvider = timeProvider;
    }

    public IReadOnlyList<YoloImportResult> Process(IReadOnlyList<AiImportAnalysisDto> analyses)
    {
        ArgumentNullException.ThrowIfNull(analyses);
        var results = new List<YoloImportResult>();
        var eligible = analyses.Where(IsCompleteProposal).OrderBy(x => x.AnalysisId, StringComparer.Ordinal).ToArray();
        if (eligible.Length == 0) return results;

        // A completed import changes Lidarr track/file state and therefore makes every
        // other analysis snapshot stale. Execute one snapshot, then require a fresh
        // discovery before considering the next album.
        results.Add(ProcessOne(eligible[0]));
        results.AddRange(eligible.Skip(1).Select(x => new YoloImportResult(
            x.AnalysisId,
            "deferred",
            "Another YOLO import was processed first; this album requires a fresh discovery snapshot before automatic execution.")));
        return results;
    }

    private YoloImportResult ProcessOne(AiImportAnalysisDto analysis)
    {
        CreatedApproval created;
        try
        {
            created = _approval.ApproveAsync(analysis.AnalysisId, ApprovalLifetime, CancellationToken.None)
                .GetAwaiter().GetResult();
        }
        catch (Exception exception)
        {
            return new YoloImportResult(
                analysis.AnalysisId,
                "approval-failed",
                $"YOLO automatic approval failed safely: {SafeMessage(exception)}");
        }

        ImportExecutionStarted started;
        try
        {
            started = _execution.ExecuteAsync(
                    created.ApprovalId, string.Empty, CancellationToken.None, trustedInProcess: true)
                .GetAwaiter().GetResult();
        }
        catch (ImportOutcomeIndeterminateException exception)
        {
            return new YoloImportResult(
                analysis.AnalysisId,
                "indeterminate",
                $"YOLO execution crossed the durable boundary and requires reconciliation: {SafeMessage(exception)}",
                created.ApprovalId);
        }
        catch (Exception exception)
        {
            return new YoloImportResult(
                analysis.AnalysisId,
                "execution-failed",
                $"YOLO execution failed: {SafeMessage(exception)}",
                created.ApprovalId);
        }

        var deadline = _timeProvider.GetUtcNow() + ReconciliationTimeout;
        while (true)
        {
            try
            {
                var reconciled = _reconciliation.ReconcileAsync(
                        created.ApprovalId, string.Empty, CancellationToken.None, trustedInProcess: true)
                    .GetAwaiter().GetResult();
                if (reconciled.Status == ImportReconciliationStatus.Succeeded)
                {
                    return new YoloImportResult(
                        analysis.AnalysisId,
                        "complete",
                        reconciled.Message,
                        created.ApprovalId,
                        started.CommandId);
                }
            }
            catch (Exception exception)
            {
                return new YoloImportResult(
                    analysis.AnalysisId,
                    "reconciliation-failed",
                    $"YOLO reconciliation failed and execution must not be retried: {SafeMessage(exception)}",
                    created.ApprovalId,
                    started.CommandId);
            }

            if (_timeProvider.GetUtcNow() >= deadline)
            {
                return new YoloImportResult(
                    analysis.AnalysisId,
                    "pending",
                    "YOLO execution was submitted but did not reconcile within five minutes; reconcile the existing approval before retrying.",
                    created.ApprovalId,
                    started.CommandId);
            }
            Task.Delay(PollInterval, _timeProvider, CancellationToken.None).GetAwaiter().GetResult();
        }
    }

    private static bool IsCompleteProposal(AiImportAnalysisDto analysis)
    {
        var proposal = analysis.Proposal;
        if (proposal.Status != ProposalStatus.Proposed || proposal.ArtistId is not > 0 ||
            proposal.AlbumId is not > 0 || proposal.ReleaseId is not > 0 ||
            analysis.Files.Count == 0 || proposal.Tracks.Count != analysis.Files.Count)
            return false;
        var sources = analysis.Files.Select(x => x.Id).ToHashSet(StringComparer.Ordinal);
        return proposal.Tracks.Select(x => x.SourceFileId).ToHashSet(StringComparer.Ordinal).SetEquals(sources) &&
               proposal.Tracks.Select(x => x.TrackId).Distinct().Count() == proposal.Tracks.Count;
    }

    private static string SafeMessage(Exception exception)
    {
        var message = exception.Message.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return message.Length <= 500 ? message : message[..500];
    }
}
