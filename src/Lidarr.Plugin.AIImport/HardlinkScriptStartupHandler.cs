using NLog;
using NzbDrone.Common.EnvironmentInfo;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Lifecycle;
using NzbDrone.Core.Messaging.Events;

namespace Lidarr.Plugin.AIImport;

public sealed class HardlinkScriptStartupHandler : IHandle<ApplicationStartingEvent>
{
    private readonly IAppFolderInfo _appFolderInfo;
    private readonly Logger _logger;

    public HardlinkScriptStartupHandler(IAppFolderInfo appFolderInfo, Logger logger)
    {
        _appFolderInfo = appFolderInfo;
        _logger = logger;
    }

    public void Handle(ApplicationStartingEvent message)
    {
        var pluginDirectory = Path.Combine(
            _appFolderInfo.GetPluginPath(),
            "mshihadeh1",
            "Lidarr.Plugin.AIImport");

        if (!HardlinkScriptInstaller.EnsureExecutable(pluginDirectory))
        {
            _logger.Error("AI Import hardlink script is missing, is a symbolic link, or could not be made executable: {0}",
                Path.Combine(pluginDirectory, HardlinkScriptInstaller.ScriptName));
        }
    }
}
