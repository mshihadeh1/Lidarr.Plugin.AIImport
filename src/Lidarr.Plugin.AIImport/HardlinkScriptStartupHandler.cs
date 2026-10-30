using NLog;
using NzbDrone.Common.EnvironmentInfo;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Lifecycle;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.Configuration;
using Lidarr.Plugin.AIImport.Filesystem;
using Lidarr.Plugin.AIImport.Workflows;

namespace Lidarr.Plugin.AIImport;

public sealed class HardlinkScriptStartupHandler : IHandle<ApplicationStartingEvent>
{
    private readonly IAppFolderInfo _appFolderInfo;
    private readonly Logger _logger;
    private readonly IConfigService? _config;

    public HardlinkScriptStartupHandler(IAppFolderInfo appFolderInfo, Logger logger)
    {
        _appFolderInfo = appFolderInfo;
        _logger = logger;
    }

    public HardlinkScriptStartupHandler(IAppFolderInfo appFolderInfo, IConfigService config, Logger logger)
        : this(appFolderInfo, logger) => _config = config;

    public void Handle(ApplicationStartingEvent message)
    {
        var statePaths = new StandaloneStatePaths(_appFolderInfo);
        statePaths.ConfigureAuthorizationEnvironment();
        var pluginDirectory = Path.Combine(
            _appFolderInfo.GetPluginPath(),
            "mshihadeh1",
            "Lidarr.Plugin.AIImport");

        if (!HardlinkScriptInstaller.EnsureExecutable(pluginDirectory))
        {
            _logger.Error("AI Import hardlink script is missing, is a symbolic link, or could not be made executable: {0}",
                Path.Combine(pluginDirectory, HardlinkScriptInstaller.ScriptName));
        }
        try
        {
            SealedNativeHelper.Install(Path.Combine(pluginDirectory, HardlinkScriptInstaller.ScriptName));
            if (_config is not null && (!_config.UseScriptImport ||
                !string.Equals(_config.ScriptImportPath, SealedNativeHelper.ExecutionPath, StringComparison.Ordinal)))
                _logger.Error("AI Import execution is disabled: Use Script Import must be enabled and Script Import Path must be exactly {0}", SealedNativeHelper.ExecutionPath);
        }
        catch (Exception exception)
        {
            _logger.Error(exception, "AI Import sealed native helper installation failed; execution is disabled");
        }
    }
}
