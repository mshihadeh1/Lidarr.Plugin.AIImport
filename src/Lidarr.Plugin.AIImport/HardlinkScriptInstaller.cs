namespace Lidarr.Plugin.AIImport;

public static class HardlinkScriptInstaller
{
    public const string ScriptName = "lidarr-hardlink-import";

    public static bool EnsureExecutable(string pluginDirectory)
    {
        var scriptPath = Path.Combine(pluginDirectory, ScriptName);
        var script = new FileInfo(scriptPath);
        if (!script.Exists || script.LinkTarget is not null) return false;
        if (OperatingSystem.IsWindows()) return false;

        try
        {
            File.SetUnixFileMode(
                scriptPath,
                UnixFileMode.UserRead |
                UnixFileMode.UserExecute |
                UnixFileMode.GroupRead |
                UnixFileMode.GroupExecute |
                UnixFileMode.OtherRead |
                UnixFileMode.OtherExecute);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            return false;
        }
    }
}
