using System.Text;
using Lidarr.Plugin.AIImport.Ui;
using Microsoft.AspNetCore.Mvc;

namespace Lidarr.Plugin.AIImport.Tests;

[TestFixture]
public sealed class AiImportWorkspaceMapperTests
{
    [Test]
    public void Handles_only_the_embedded_workspace_path()
    {
        var mapper = new AiImportWorkspaceMapper();

        Assert.Multiple(() =>
        {
            Assert.That(mapper.CanHandle("/plugins/mshihadeh1/ai-import-workspace.html"), Is.True);
            Assert.That(mapper.CanHandle("/PLUGINS/MSHIHADEH1/AI-IMPORT-WORKSPACE.HTML"), Is.True);
            Assert.That(mapper.CanHandle("/"), Is.False);
            Assert.That(mapper.CanHandle("/index.html"), Is.False);
            Assert.That(mapper.CanHandle("/ai-import.js"), Is.False);
        });
    }

    [Test]
    public async Task Serves_a_self_contained_safety_gated_workspace()
    {
        var mapper = new AiImportWorkspaceMapper();

        var response = await mapper.GetResponse("/plugins/mshihadeh1/ai-import-workspace.html");
        var file = response as FileContentResult;
        Assert.That(file, Is.Not.Null);
        var html = Encoding.UTF8.GetString(file!.FileContents);

        Assert.Multiple(() =>
        {
            Assert.That(file.ContentType, Is.EqualTo("text/html"));
            Assert.That(html, Does.Contain("AI Import Workspace"));
            Assert.That(html, Does.Contain("Discover failed imports"));
            Assert.That(html, Does.Contain("function hasCompleteUniqueMapping"));
            Assert.That(html, Does.Contain("const proposed = analysis.proposal && analysis.proposal.status === 'proposed' && hasCompleteUniqueMapping(analysis);"));
            Assert.That(html, Does.Contain("I reviewed every source-to-track mapping"));
            Assert.That(html, Does.Contain("Create manually reviewed proposal"));
            Assert.That(html, Does.Contain("Manual override: choose one verified Lidarr release"));
            Assert.That(html, Does.Contain("Add verified artist and album"));
            Assert.That(html, Does.Contain("perform no file import until you rediscover"));
            Assert.That(html, Does.Contain("Type IMPORT to enable execution"));
            Assert.That(html, Does.Contain("function safeActionResult"));
            Assert.That(html, Does.Contain("function safeHttpError"));
            Assert.That(html, Does.Contain("new DOMParser().parseFromString"));
            Assert.That(html, Does.Contain("detail.length > 500"));
            Assert.That(html, Does.Contain("actionName = 'discover-start'"));
            Assert.That(html, Does.Contain("actionName = 'discover-retry'"));
            Assert.That(html, Does.Contain("actionName = 'discover-resume'"));
            Assert.That(html, Does.Contain("action(actionName)"));
            Assert.That(html, Does.Contain("action('discover-status')"));
            Assert.That(html, Does.Contain("action('discover-stop')"));
            Assert.That(html, Does.Contain("Stop after current operation"));
            Assert.That(html, Does.Contain("safe to close this tab"));
            Assert.That(html, Does.Contain("await resumeDiscovery()"));
            Assert.That(html, Does.Contain("id=\"run-progress\""));
            Assert.That(html, Does.Contain("id=\"album-progress\""));
            Assert.That(html, Does.Contain("function renderJobProgress"));
            Assert.That(html, Does.Contain("--lidarr-green:#00a65b"));
            Assert.That(html, Does.Contain("--page:#202020"));
            Assert.That(html, Does.Contain("role=\"progressbar\""));
            Assert.That(html, Does.Contain("role=\"tablist\""));
            Assert.That(html, Does.Contain("id=\"ai-import-tab\""));
            Assert.That(html, Does.Contain("id=\"manual-review-tab\""));
            Assert.That(html, Does.Contain("id=\"import-history-tab\""));
            Assert.That(html, Does.Contain("function activateTab"));
            Assert.That(html, Does.Contain("Remove from Lidarr and delete hardlinks"));
            Assert.That(html, Does.Contain("Source files are always retained"));
            Assert.That(html, Does.Contain("Rollback response failed. Its outcome may be indeterminate; do not retry"));
            Assert.That(html, Does.Not.Contain("Rollback stopped:"));
            Assert.That(html, Does.Not.Contain("confirmation.disabled = false;\n              rollback.disabled"));
            Assert.That(html, Does.Contain("analyses.length + diagnostics.length"));
            Assert.That(html, Does.Contain("analyses.length || diagnostics.length"));
            Assert.That(html, Does.Contain(".tab-panel:focus-visible"));
            Assert.That(html, Does.Contain("commandStatus: value.commandStatus"));
            Assert.That(html, Does.Contain("executeConfirmation: 'IMPORT'"));
            Assert.That(html, Does.Contain("providerId"));
            Assert.That(html, Does.Contain("show('reconcile')"));
            Assert.That(html, Does.Not.Contain("YOUR_LIDARR_API_KEY"));
        });
    }
}
