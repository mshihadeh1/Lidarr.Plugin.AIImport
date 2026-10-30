using Lidarr.Plugin.AIImport.Approval;
using Lidarr.Plugin.AIImport.Authorization;
using Lidarr.Plugin.AIImport.Execution;
using Lidarr.Plugin.AIImport.Filesystem;
using Lidarr.Plugin.AIImport.Lidarr;
using Lidarr.Plugin.AIImport.Workflows;
using NzbDrone.Common.EnvironmentInfo;

namespace Lidarr.Plugin.AIImport.Tests;

[TestFixture]
public sealed class StandaloneCompositionTests
{
    [Test]
    public void Persistent_standalone_state_is_rooted_under_lidarr_app_data()
    {
        using var temp = new TempDirectory();
        var app = new FakeAppFolderInfo(temp.Path);
        var paths = new StandaloneStatePaths(app);

        Assert.Multiple(() =>
        {
            Assert.That(paths.Root, Is.EqualTo(Path.Combine(temp.Path, "ai-import")));
            Assert.That(paths.ApprovalStore, Is.EqualTo(Path.Combine(temp.Path, "ai-import", "approvals.json")));
            Assert.That(paths.AnalysisStore, Is.EqualTo(Path.Combine(temp.Path, "ai-import", "analyses.json")));
            Assert.That(paths.AuthorizationDirectory, Is.EqualTo(Path.Combine(temp.Path, "ai-import", "authorizations")));
        });
    }

    [Test]
    public void Standalone_service_graph_has_DI_constructible_concrete_adapters()
    {
        Assert.That(typeof(IApprovedPlanProvider).IsAssignableFrom(typeof(StandaloneApprovedPlanProvider)), Is.True);
        Assert.That(typeof(ILidarrExecutionClient).IsAssignableFrom(typeof(DirectLidarrExecutionClient)), Is.True);
        Assert.That(typeof(ILidarrReconciliationClient).IsAssignableFrom(typeof(DirectLidarrReconciliationClient)), Is.True);
        Assert.That(typeof(IStandaloneAiImportWorkflow).IsAssignableFrom(typeof(StandaloneAiImportWorkflow)), Is.True);

        AssertDiConstructor(typeof(ApprovalStore));
        AssertDiConstructor(typeof(StandaloneAnalysisStore));
        AssertDiConstructor(typeof(TransferAuthorizationStore));
        AssertDiConstructor(typeof(NativeFileIdentityProvider));
        AssertDiConstructor(typeof(NativeTransferResultVerifier));
    }

    [Test]
    public void Safety_validation_is_hardlink_only_and_fails_closed()
    {
        var valid = new InProcessImportSafetySettings(
            CopyUsingHardlinks: true,
            UseScriptImport: true,
            ScriptImportPath: SealedNativeHelper.ExecutionPath,
            SetPermissionsLinux: false,
            FileDate: "None",
            ImportExtraFiles: false,
            WriteAudioTags: "No",
            ScrubAudioTags: false,
            EmbedCoverArt: false);

        Assert.DoesNotThrow(() => InProcessImportSafety.Validate(valid, "3.1.2.4913"));
        Assert.Throws<InvalidOperationException>(() =>
            InProcessImportSafety.Validate(valid with { CopyUsingHardlinks = false }, "3.1.2.4913"));
        Assert.Throws<InvalidOperationException>(() =>
            InProcessImportSafety.Validate(valid with { ScriptImportPath = "/config/helper" }, "3.1.2.4913"));
        Assert.Throws<InvalidOperationException>(() =>
            InProcessImportSafety.Validate(valid with { WriteAudioTags = "NewFiles" }, "3.1.2.4913"));
    }

    private static void AssertDiConstructor(Type type)
    {
        Assert.That(type.GetConstructors().Any(ctor =>
            ctor.GetParameters().Length > 0 &&
            ctor.GetParameters().All(parameter => parameter.ParameterType != typeof(string) &&
                                                       parameter.ParameterType != typeof(TimeSpan))),
            Is.True, $"{type.Name} requires a public constructor whose dependencies can be resolved by Lidarr DI.");
    }

    private sealed class FakeAppFolderInfo(string appDataFolder) : IAppFolderInfo
    {
        public string AppDataFolder { get; } = appDataFolder;
        public string TempFolder => Path.GetTempPath();
        public string StartUpFolder => AppContext.BaseDirectory;
    }

    private sealed class TempDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ai-import-composition-" + Guid.NewGuid().ToString("N"));
        public TempDirectory() => Directory.CreateDirectory(Path);
        public void Dispose() => Directory.Delete(Path, true);
    }
}
