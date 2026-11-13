using Lidarr.Plugin.AIImport.Commands;
using Lidarr.Plugin.AIImport.Configuration;
using NLog;
using NzbDrone.Core.Download.TrackedDownloads;
using NzbDrone.Core.Messaging.Commands;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.Notifications;

namespace Lidarr.Plugin.AIImport.Workflows;

public sealed class YoloTrackedDownloadHandler : IHandle<TrackedDownloadRefreshedEvent>
{
    private readonly INotificationRepository _notifications;
    private readonly IAiImportDiscoveryJobService _discoveryJobs;
    private readonly Logger _logger;
    private readonly object _gate = new();
    private string? _lastQueuedSnapshot;

    public YoloTrackedDownloadHandler(
        INotificationRepository notifications,
        IAiImportDiscoveryJobService discoveryJobs,
        Logger logger)
    {
        _notifications = notifications;
        _discoveryJobs = discoveryJobs;
        _logger = logger;
    }

    public void Handle(TrackedDownloadRefreshedEvent message)
    {
        ArgumentNullException.ThrowIfNull(message);
        var blocked = message.TrackedDownloads
            .Where(x => x.State is TrackedDownloadState.ImportBlocked or TrackedDownloadState.ImportFailed)
            .Where(x => !string.IsNullOrWhiteSpace(x.DownloadItem?.DownloadId))
            .OrderBy(x => x.DownloadItem.DownloadId, StringComparer.Ordinal)
            .ThenBy(x => x.State)
            .ToArray();
        if (blocked.Length == 0)
        {
            lock (_gate) _lastQueuedSnapshot = null;
            return;
        }

        var providers = _notifications.All()
            .Where(x => string.Equals(x.Implementation, nameof(AiImportConnector), StringComparison.Ordinal) &&
                        x.Settings is AiImportSettings { YoloMode: true })
            .ToArray();
        if (providers.Length == 0) return;
        if (providers.Length != 1)
        {
            _logger.Error("AI Import YOLO trigger requires exactly one saved YOLO-enabled AI Import provider; found {0}", providers.Length);
            return;
        }
        var settings = (AiImportSettings)providers[0].Settings;
        if (!settings.Validate().IsValid)
        {
            _logger.Error("AI Import YOLO trigger skipped invalid saved provider settings");
            return;
        }

        var snapshot = string.Join("|", blocked.Select(x => $"{x.DownloadItem.DownloadId}:{x.State}"));
        lock (_gate)
        {
            if (string.Equals(snapshot, _lastQueuedSnapshot, StringComparison.Ordinal)) return;
            _lastQueuedSnapshot = snapshot;
        }

        try
        {
            var job = _discoveryJobs.Start(providers[0].Id);
            if (job.State == DiscoveryJobState.Indeterminate)
            {
                _logger.Warn("AI Import YOLO trigger remains blocked by indeterminate discovery job {0}", job.JobId);
                return;
            }
            _logger.Info("AI Import YOLO discovery queued for {0} blocked or failed download(s)", blocked.Length);
        }
        catch
        {
            lock (_gate)
            {
                if (string.Equals(snapshot, _lastQueuedSnapshot, StringComparison.Ordinal)) _lastQueuedSnapshot = null;
            }
            throw;
        }
    }
}
