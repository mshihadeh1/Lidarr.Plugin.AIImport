using System.Reflection;
using System.Text;
using Lidarr.Http.Frontend.Mappers;
using Microsoft.AspNetCore.Mvc;

namespace Lidarr.Plugin.AIImport.Ui;

public sealed class AiImportWorkspaceMapper : IMapHttpRequestsToDisk
{
    internal const string WorkspacePath = "/plugins/mshihadeh1/ai-import-workspace.html";
    private const string ResourceName = "Lidarr.Plugin.AIImport.Ui.ai-import.html";

    public bool CanHandle(string resourceUrl) =>
        string.Equals(resourceUrl, WorkspacePath, StringComparison.OrdinalIgnoreCase);

    public string Map(string resourceUrl) =>
        CanHandle(resourceUrl) ? ResourceName : string.Empty;

    public async Task<IActionResult> GetResponse(string resourceUrl)
    {
        if (!CanHandle(resourceUrl))
            return null!;

        await using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException("The embedded AI Import workspace is missing.");
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer).ConfigureAwait(false);

        return new FileContentResult(buffer.ToArray(), "text/html")
        {
            FileDownloadName = null
        };
    }
}
