using NzbDrone.Common.EnvironmentInfo;

namespace Lidarr.Plugin.AIImport.Workflows;

public sealed class StandaloneStatePaths
{
    public StandaloneStatePaths(IAppFolderInfo appFolderInfo)
    {
        ArgumentNullException.ThrowIfNull(appFolderInfo);
        var appData = Path.GetFullPath(appFolderInfo.AppDataFolder);
        Root = Path.GetFullPath(Path.Combine(appData, "ai-import"));
        if (!IsUnder(Root, appData))
            throw new InvalidOperationException("AI Import state storage must be under Lidarr app data.");
        ApprovalStore = Path.Combine(Root, "approvals.json");
        AnalysisStore = Path.Combine(Root, "analyses.json");
        AuthorizationDirectory = Path.Combine(Root, "authorizations");
    }

    public string Root { get; }
    public string ApprovalStore { get; }
    public string AnalysisStore { get; }
    public string AuthorizationDirectory { get; }

    public void ConfigureAuthorizationEnvironment()
    {
        var configured = Environment.GetEnvironmentVariable("AIIMPORT_AUTHORIZATION_DIR");
        if (!string.IsNullOrWhiteSpace(configured) &&
            !string.Equals(Path.GetFullPath(configured), AuthorizationDirectory, StringComparison.Ordinal))
            throw new InvalidOperationException("AIIMPORT_AUTHORIZATION_DIR must resolve to Lidarr app data storage.");
        Environment.SetEnvironmentVariable("AIIMPORT_AUTHORIZATION_DIR", AuthorizationDirectory);
        if (!string.Equals(Environment.GetEnvironmentVariable("AIIMPORT_AUTHORIZATION_DIR"), AuthorizationDirectory, StringComparison.Ordinal))
            throw new InvalidOperationException("AIIMPORT_AUTHORIZATION_DIR could not be established in-process.");
    }

    private static bool IsUnder(string path, string root)
    {
        var relative = Path.GetRelativePath(root, path);
        return !Path.IsPathRooted(relative) && relative != ".." &&
               !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal);
    }
}
