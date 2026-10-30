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
            Assert.That(html, Does.Contain("Type IMPORT to enable execution"));
            Assert.That(html, Does.Contain("function safeActionResult"));
            Assert.That(html, Does.Contain("commandStatus: value.commandStatus"));
            Assert.That(html, Does.Contain("executeConfirmation: 'IMPORT'"));
            Assert.That(html, Does.Contain("providerId"));
            Assert.That(html, Does.Contain("show('reconcile')"));
            Assert.That(html, Does.Not.Contain("YOUR_LIDARR_API_KEY"));
        });
    }
}
