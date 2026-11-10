using Lidarr.Plugin.AIImport.Approval;
using Lidarr.Plugin.AIImport.Commands;
using Lidarr.Plugin.AIImport.Configuration;
using Lidarr.Plugin.AIImport.Execution;
using NLog;
using NzbDrone.Core.Lifecycle;
using NzbDrone.Core.Messaging.Commands;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.Notifications;

namespace Lidarr.Plugin.AIImport.Workflows;

public sealed class YoloAutomationService :
    IHandle<ApplicationStartedEvent>,
    IHandle<ApplicationShutdownRequested>,
    IDisposable
{
    private static readonly TimeSpan TickInterval = TimeSpan.FromMinutes(1);
    private readonly INotificationRepository _notifications;
    private readonly IManageCommandQueue _commands;
    private readonly ApprovalStore _approvals;
    private readonly ImportReconciliationCoordinator _reconciliation;
    private readonly YoloRuntimeStore _runtime;
    private readonly Logger _logger;
    private Timer? _timer;
    private int _tickRunning;
    private CancellationTokenSource? _shutdown;

    public YoloAutomationService(
        INotificationRepository notifications,
        IManageCommandQueue commands,
        ApprovalStore approvals,
        ImportReconciliationCoordinator reconciliation,
        YoloRuntimeStore runtime,
        Logger logger)
    {
        _notifications = notifications;
        _commands = commands;
        _approvals = approvals;
        _reconciliation = reconciliation;
        _runtime = runtime;
        _logger = logger;
    }

    public void Handle(ApplicationStartedEvent message)
    {
        _shutdown?.Dispose();
        _shutdown = new CancellationTokenSource();
        RecoverAndReconcile(_shutdown.Token);
        _timer?.Dispose();
        _timer = new Timer(_ => Tick(), null, TickInterval, TickInterval);
    }

    public void Handle(ApplicationShutdownRequested message) => Stop();

    public void Dispose()
    {
        Stop();
        GC.SuppressFinalize(this);
    }

    private void Tick()
    {
        if (Interlocked.Exchange(ref _tickRunning, 1) != 0) return;
        try
        {
            if (ReconcilePending(_shutdown?.Token ?? CancellationToken.None)) return;
            QueuePeriodicDiscovery(_shutdown?.Token ?? CancellationToken.None);
        }
        catch (OperationCanceledException) when (_shutdown?.IsCancellationRequested == true)
        {
        }
        catch (Exception exception)
        {
            _logger.Error(exception, "AI Import YOLO periodic discovery failed safely");
        }
        finally
        {
            Volatile.Write(ref _tickRunning, 0);
        }
    }

    private void RecoverAndReconcile(CancellationToken cancellationToken)
    {
        try
        {
            var recovered = _approvals.RecoverInterruptedExecutionsAsync(cancellationToken).GetAwaiter().GetResult();
            if (recovered != 0)
                _logger.Warn("AI Import recovered {0} interrupted execution(s) as indeterminate", recovered);
            ReconcilePending(cancellationToken);
        }
        catch (Exception exception)
        {
            _logger.Error(exception, "AI Import startup recovery failed safely");
        }
    }

    private bool ReconcilePending(CancellationToken cancellationToken)
    {
        var unresolved = false;
        var pending = _approvals.GetPendingReconciliationAsync(cancellationToken).GetAwaiter().GetResult();
        foreach (var approval in pending)
        {
            try
            {
                var result = _reconciliation.ReconcileAsync(
                        approval.ApprovalId, string.Empty, cancellationToken, trustedInProcess: true)
                    .GetAwaiter().GetResult();
                _logger.Info("AI Import recovery reconciliation approval={0} status={1}",
                    approval.ApprovalId, result.Status);
                unresolved |= result.Status != ImportReconciliationStatus.Succeeded;
            }
            catch (Exception exception)
            {
                unresolved = true;
                _logger.Warn(exception,
                    "AI Import recovery reconciliation could not prove approval {0}; it remains non-retryable",
                    approval.ApprovalId);
            }
        }
        return unresolved;
    }

    private void QueuePeriodicDiscovery(CancellationToken cancellationToken)
    {
        var providers = _notifications.All()
            .Where(definition => string.Equals(definition.Implementation, nameof(AiImportConnector), StringComparison.Ordinal) &&
                                 definition.Settings is AiImportSettings { YoloMode: true } settings &&
                                 AiImportSettingsValidator.SplitScanRoots(settings.ScanRoots).Length != 0)
            .ToArray();
        if (providers.Length == 0) return;
        if (providers.Length != 1)
        {
            _logger.Error("AI Import periodic YOLO scan requires exactly one eligible provider; found {0}", providers.Length);
            return;
        }

        var settings = (AiImportSettings)providers[0].Settings;
        if (!settings.Validate().IsValid)
        {
            _logger.Error("AI Import periodic YOLO scan skipped invalid saved provider settings");
            return;
        }
        var now = DateTimeOffset.UtcNow;
        var state = _runtime.ReadAsync(cancellationToken).GetAwaiter().GetResult();
        if (state.LastPeriodicDiscoveryQueuedAt is { } last &&
            now - last < TimeSpan.FromMinutes(settings.YoloScanIntervalMinutes))
            return;

        if (_commands.All().Any(command =>
                string.Equals(command.Name, new AiImportDiscoverCommand().Name, StringComparison.Ordinal) &&
                command.Status is CommandStatus.Queued or CommandStatus.Started))
            return;

        _commands.Push(new AiImportDiscoverCommand { ProviderDefinitionId = providers[0].Id }, CommandPriority.Low);
        // The command is already durable. Complete the timestamp write during graceful
        // shutdown so the next process does not immediately enqueue the same scan.
        _runtime.RecordPeriodicDiscoveryQueuedAsync(now, CancellationToken.None).GetAwaiter().GetResult();
        _logger.Info("AI Import periodic YOLO discovery queued for provider {0}", providers[0].Id);
    }

    private void Stop()
    {
        _shutdown?.Cancel();
        _timer?.Dispose();
        _timer = null;
        _shutdown?.Dispose();
        _shutdown = null;
    }
}
