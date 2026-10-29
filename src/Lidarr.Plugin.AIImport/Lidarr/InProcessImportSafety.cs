using System.Security.Cryptography;
using System.Text.Json;
using Lidarr.Plugin.AIImport.Discovery;
using Lidarr.Plugin.AIImport.Filesystem;
using NzbDrone.Core.Configuration;

namespace Lidarr.Plugin.AIImport.Lidarr;

public sealed record InProcessImportSafetySettings(
    bool CopyUsingHardlinks,
    bool UseScriptImport,
    string ScriptImportPath,
    bool SetPermissionsLinux,
    string FileDate,
    bool ImportExtraFiles,
    string WriteAudioTags,
    bool ScrubAudioTags,
    bool EmbedCoverArt);

public sealed class InProcessImportSafety
{
    private readonly IConfigService _config;
    private readonly IInProcessLidarrGateway _gateway;

    public InProcessImportSafety(IConfigService config, IInProcessLidarrGateway gateway)
    {
        _config = config;
        _gateway = gateway;
    }

    public string CaptureFingerprint()
    {
        var settings = new InProcessImportSafetySettings(
            _config.CopyUsingHardlinks,
            _config.UseScriptImport,
            _config.ScriptImportPath,
            _config.SetPermissionsLinux,
            _config.FileDate.ToString(),
            _config.ImportExtraFiles,
            _config.WriteAudioTags.ToString(),
            _config.ScrubAudioTags,
            _config.EmbedCoverArt);
        Validate(settings, _gateway.Version);
        var attestor = new NativeHelperAttestor(SealedNativeHelper.ExecutionPath);
        var canonical = JsonSerializer.SerializeToUtf8Bytes(new
        {
            SchemaVersion = 1,
            LidarrVersion = _gateway.Version,
            Settings = settings,
            NativeHelperSha256 = attestor.ExpectedSha256
        });
        return Convert.ToHexString(SHA256.HashData(canonical)).ToLowerInvariant();
    }

    public static void Validate(InProcessImportSafetySettings settings, string lidarrVersion)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var blockers = new List<string>();
        if (!string.Equals(lidarrVersion, FailedImportDiscoveryWorkflow.CompatibleLidarrVersion, StringComparison.Ordinal))
            blockers.Add($"Lidarr {FailedImportDiscoveryWorkflow.CompatibleLidarrVersion} is required.");
        if (!settings.CopyUsingHardlinks) blockers.Add("Copy Using Hardlinks must be enabled.");
        if (!settings.UseScriptImport) blockers.Add("Script Import must be enabled.");
        if (!string.Equals(settings.ScriptImportPath, SealedNativeHelper.ExecutionPath, StringComparison.Ordinal))
            blockers.Add($"Script Import Path must be exactly {SealedNativeHelper.ExecutionPath}.");
        if (settings.SetPermissionsLinux) blockers.Add("Linux permission changes must be disabled.");
        if (!string.Equals(settings.FileDate, "None", StringComparison.OrdinalIgnoreCase))
            blockers.Add("File date changes must be disabled.");
        if (settings.ImportExtraFiles) blockers.Add("Extra-file import must be disabled.");
        if (!string.Equals(settings.WriteAudioTags, "No", StringComparison.OrdinalIgnoreCase))
            blockers.Add("Audio tag writing must be disabled.");
        if (settings.ScrubAudioTags) blockers.Add("Audio tag scrubbing must be disabled.");
        if (settings.EmbedCoverArt) blockers.Add("Embedded cover art must be disabled.");
        if (blockers.Count != 0)
            throw new InvalidOperationException("Strict hardlink-only import prerequisites are not satisfied: " + string.Join(" ", blockers));
    }
}
