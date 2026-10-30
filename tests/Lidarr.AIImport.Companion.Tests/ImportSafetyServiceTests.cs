using Lidarr.AIImport.Companion.Configuration;
using Lidarr.AIImport.Companion.Models;
using Lidarr.AIImport.Companion.Safety;

namespace Lidarr.AIImport.Companion.Tests;

[TestFixture]
public sealed class ImportSafetyServiceTests
{
    private const string ScriptPath = "/config/lidarr-ai-import/lidarr-hardlink-import";
    private static readonly LidarrSystemStatus CompatibleStatus = new("3.1.2.4913", "3.1.2.4913-ls1", "plugins");

    [Test]
    public void Ready_only_when_every_source_mutation_and_copy_fallback_guard_is_active()
    {
        var service = new ImportSafetyService(new CompanionOptions
        {
            HardlinkScriptPath = ScriptPath,
            AuthorizationDeploymentConfirmed = true
        });
        var configuration = new ImportSafetyConfiguration(
            CopyUsingHardlinks: true,
            UseScriptImport: true,
            ScriptImportPath: ScriptPath,
            SetPermissionsLinux: false,
            FileDate: "none",
            ImportExtraFiles: false,
            WriteAudioTags: "no",
            ScrubAudioTags: false,
            EmbedCoverArt: false);

        var result = service.Assess(CompatibleStatus, configuration);

        Assert.Multiple(() =>
        {
            Assert.That(result.ConfigurationPrerequisitesSatisfied, Is.True);
            Assert.That(result.ExecutionEnabled, Is.True);
            Assert.That(result.Blockers, Is.Empty);
            Assert.That(result.Limitations, Has.Some.Contains("pinned Lidarr"));
            Assert.That(result.Limitations, Has.Some.Contains("per-file"));
            Assert.That(result.Limitations, Has.Some.Contains("test one expendable album"));
        });
    }

    [Test]
    public void Shared_authorization_deployment_must_be_explicitly_confirmed()
    {
        var service = new ImportSafetyService(new CompanionOptions { HardlinkScriptPath = ScriptPath });
        var configuration = new ImportSafetyConfiguration(true, true, ScriptPath, false, "none", false, "no", false, false);

        var result = service.Assess(CompatibleStatus, configuration);

        Assert.Multiple(() =>
        {
            Assert.That(result.ExecutionEnabled, Is.False);
            Assert.That(result.Blockers, Has.Some.Contains("AUTHORIZATION_DEPLOYMENT_CONFIRMED"));
        });
    }

    [Test]
    public void Reports_every_unsafe_setting_without_mutating_lidarr()
    {
        var service = new ImportSafetyService(new CompanionOptions { HardlinkScriptPath = ScriptPath });
        var configuration = new ImportSafetyConfiguration(
            CopyUsingHardlinks: false,
            UseScriptImport: false,
            ScriptImportPath: "/wrong/script",
            SetPermissionsLinux: true,
            FileDate: "albumReleaseDate",
            ImportExtraFiles: true,
            WriteAudioTags: "allFiles",
            ScrubAudioTags: true,
            EmbedCoverArt: true);

        var result = service.Assess(CompatibleStatus, configuration);

        Assert.Multiple(() =>
        {
            Assert.That(result.ConfigurationPrerequisitesSatisfied, Is.False);
            Assert.That(result.ExecutionEnabled, Is.False);
            Assert.That(result.Blockers, Has.Some.Contains("Copy Using Hardlinks"));
            Assert.That(result.Blockers, Has.Some.Contains("script import"));
            Assert.That(result.Blockers, Has.Some.Contains(ScriptPath));
            Assert.That(result.Blockers, Has.Some.Contains("permissions"));
            Assert.That(result.Blockers, Has.Some.Contains("file date"));
            Assert.That(result.Blockers, Has.Some.Contains("extra files"));
            Assert.That(result.Blockers, Has.Some.Contains("audio tag"));
        });
    }

    [Test]
    public void Missing_expected_script_path_is_never_ready()
    {
        var service = new ImportSafetyService(new CompanionOptions());
        var configuration = new ImportSafetyConfiguration(true, true, ScriptPath, false, "none", false, "no", false, false);

        var result = service.Assess(CompatibleStatus, configuration);

        Assert.That(result.ConfigurationPrerequisitesSatisfied, Is.False);
        Assert.That(result.ExecutionEnabled, Is.False);
        Assert.That(result.Blockers, Has.Some.Contains("HARDLINK_SCRIPT_PATH"));
    }

    [Test]
    public void Unsupported_lidarr_version_can_never_satisfy_prerequisites()
    {
        var service = new ImportSafetyService(new CompanionOptions { HardlinkScriptPath = ScriptPath });
        var configuration = new ImportSafetyConfiguration(true, true, ScriptPath, false, "none", false, "no", false, false);

        var result = service.Assess(new LidarrSystemStatus("3.2.0.0", "3.2.0.0", "plugins"), configuration);

        Assert.That(result.ConfigurationPrerequisitesSatisfied, Is.False);
        Assert.That(result.Blockers, Has.Some.Contains("3.1.2.4913"));
    }
}
