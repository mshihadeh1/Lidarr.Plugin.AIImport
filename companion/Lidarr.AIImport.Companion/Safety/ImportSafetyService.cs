using Lidarr.AIImport.Companion.Configuration;
using Lidarr.AIImport.Companion.Models;

namespace Lidarr.AIImport.Companion.Safety;

public sealed record ImportSafetyConfiguration(
    bool CopyUsingHardlinks,
    bool UseScriptImport,
    string ScriptImportPath,
    bool SetPermissionsLinux,
    string FileDate,
    bool ImportExtraFiles,
    string WriteAudioTags,
    bool ScrubAudioTags,
    bool EmbedCoverArt);

public sealed record ImportSafetyAssessment(
    bool ConfigurationPrerequisitesSatisfied,
    bool ExecutionEnabled,
    IReadOnlyList<string> Blockers,
    IReadOnlyList<string> Limitations);

public sealed class ImportSafetyService
{
    public const string CompatibleLidarrVersion = "3.1.2.4913";
    private readonly CompanionOptions _options;

    public ImportSafetyService(CompanionOptions options)
    {
        _options = options;
    }

    public ImportSafetyAssessment Assess(LidarrSystemStatus status, ImportSafetyConfiguration configuration)
    {
        var blockers = new List<string>();
        if (!string.Equals(status.Version, CompatibleLidarrVersion, StringComparison.Ordinal))
            blockers.Add($"Import safety is supported only for Lidarr {CompatibleLidarrVersion}.");
        if (string.IsNullOrWhiteSpace(_options.HardlinkScriptPath))
            blockers.Add("HARDLINK_SCRIPT_PATH must identify the verified hardlink-only import script inside Lidarr.");
        if (!configuration.CopyUsingHardlinks)
            blockers.Add("Lidarr Copy Using Hardlinks must be enabled.");
        if (!configuration.UseScriptImport)
            blockers.Add("Lidarr script import must be enabled.");
        if (!string.Equals(configuration.ScriptImportPath, _options.HardlinkScriptPath, StringComparison.Ordinal))
            blockers.Add($"Lidarr script import path must be exactly {_options.HardlinkScriptPath}.");
        if (configuration.SetPermissionsLinux)
            blockers.Add("Lidarr Linux file permissions changes must be disabled to avoid mutating the source inode.");
        if (!string.Equals(configuration.FileDate, "none", StringComparison.OrdinalIgnoreCase))
            blockers.Add("Lidarr file date changes must be disabled to avoid mutating the source inode.");
        if (configuration.ImportExtraFiles)
            blockers.Add("Lidarr extra files import must be disabled for hardlink-only execution.");
        if (!string.Equals(configuration.WriteAudioTags, "no", StringComparison.OrdinalIgnoreCase))
            blockers.Add("Lidarr audio tag writing must be disabled to preserve torrent payload bytes.");
        if (configuration.ScrubAudioTags)
            blockers.Add("Lidarr audio tag scrubbing must be disabled.");
        if (configuration.EmbedCoverArt)
            blockers.Add("Lidarr embedded cover art must be disabled.");
        return new ImportSafetyAssessment(
            ConfigurationPrerequisitesSatisfied: blockers.Count == 0,
            ExecutionEnabled: false,
            Blockers: blockers,
            Limitations:
            [
                "The companion verifies Lidarr configuration but does not attest the installed script bytes.",
                "Lidarr command completion does not prove that an individual file import succeeded.",
                "The shell transfer primitive cannot eliminate pathname TOCTOU races; execution remains disabled pending a descriptor-safe transfer implementation."
            ]);
    }
}
