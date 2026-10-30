using Lidarr.Plugin.AIImport;
using System.Runtime.Versioning;
using NzbDrone.Common.EnvironmentInfo;

namespace Lidarr.Plugin.AIImport.Tests;

[TestFixture]
public sealed class HardlinkScriptInstallerTests
{
    private sealed class TestAppFolderInfo(string appDataFolder) : IAppFolderInfo
    {
        public string AppDataFolder { get; } = appDataFolder;
        public string TempFolder => Path.GetTempPath();
        public string StartUpFolder => AppContext.BaseDirectory;
    }

    [Test]
    [SupportedOSPlatform("linux")]
    public void Makes_packaged_script_executable_on_unix()
    {
        var directory = Directory.CreateTempSubdirectory("lidarr-ai-import-");
        try
        {
            var script = Path.Combine(directory.FullName, HardlinkScriptInstaller.ScriptName);
            File.WriteAllText(script, "#!/bin/sh\nexit 0\n");
            File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite);

            var result = HardlinkScriptInstaller.EnsureExecutable(directory.FullName);
            var mode = File.GetUnixFileMode(script);

            Assert.Multiple(() =>
            {
                Assert.That(result, Is.True);
                Assert.That(mode, Is.EqualTo(
                    UnixFileMode.UserRead | UnixFileMode.UserExecute |
                    UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                    UnixFileMode.OtherRead | UnixFileMode.OtherExecute));
            });
        }
        finally
        {
            directory.Delete(true);
        }
    }

    [Test]
    public void Missing_script_fails_closed_without_creating_a_file()
    {
        var directory = Directory.CreateTempSubdirectory("lidarr-ai-import-");
        try
        {
            Assert.That(HardlinkScriptInstaller.EnsureExecutable(directory.FullName), Is.False);
            Assert.That(File.Exists(Path.Combine(directory.FullName, HardlinkScriptInstaller.ScriptName)), Is.False);
        }
        finally
        {
            directory.Delete(true);
        }
    }

    [Test]
    [SupportedOSPlatform("linux")]
    public void Application_starting_handler_fixes_mode_in_the_real_plugin_folder()
    {
        var appData = Directory.CreateTempSubdirectory("lidarr-ai-import-appdata-");
        try
        {
            var pluginDirectory = Path.Combine(appData.FullName, "plugins", "mshihadeh1", "Lidarr.Plugin.AIImport");
            Directory.CreateDirectory(pluginDirectory);
            var script = Path.Combine(pluginDirectory, HardlinkScriptInstaller.ScriptName);
            File.WriteAllText(script, "#!/bin/sh\nexit 0\n");
            File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite);

            var handler = new HardlinkScriptStartupHandler(
                new TestAppFolderInfo(appData.FullName),
                NLog.LogManager.GetLogger("hardlink-installer-test"));
            handler.Handle(new NzbDrone.Core.Lifecycle.ApplicationStartingEvent());

            Assert.That(File.GetUnixFileMode(script), Is.EqualTo(
                UnixFileMode.UserRead | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                UnixFileMode.OtherRead | UnixFileMode.OtherExecute));
        }
        finally
        {
            appData.Delete(true);
        }
    }

    [Test]
    [SupportedOSPlatform("linux")]
    public void Refuses_to_chmod_a_symbolic_link()
    {
        var directory = Directory.CreateTempSubdirectory("lidarr-ai-import-symlink-");
        try
        {
            var target = Path.Combine(directory.FullName, "target");
            File.WriteAllText(target, "target");
            File.SetUnixFileMode(target, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            File.CreateSymbolicLink(Path.Combine(directory.FullName, HardlinkScriptInstaller.ScriptName), target);

            var result = HardlinkScriptInstaller.EnsureExecutable(directory.FullName);

            Assert.Multiple(() =>
            {
                Assert.That(result, Is.False);
                Assert.That(File.GetUnixFileMode(target).HasFlag(UnixFileMode.UserExecute), Is.False);
            });
        }
        finally
        {
            directory.Delete(true);
        }
    }
}
