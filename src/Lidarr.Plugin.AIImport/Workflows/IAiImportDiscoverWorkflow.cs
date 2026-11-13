using Lidarr.Plugin.AIImport.Configuration;
using Lidarr.Plugin.AIImport.Discovery;
using System.Text.Json.Serialization;

namespace Lidarr.Plugin.AIImport.Workflows;

public interface IAiImportDiscoverWorkflow
{
    AiImportDiscoverResult Discover(AiImportSettings settings);
}

[method: JsonConstructor]
public sealed record AiImportDiscoverResult(
    IReadOnlyList<AiImportAnalysisDto> Analyses,
    IReadOnlyList<AiImportDiscoveryDiagnostic> Diagnostics,
    bool YoloMode = false,
    bool YoloContinuation = false)
{
    public AiImportDiscoverResult(IReadOnlyList<AiImportAnalysisDto> analyses)
        : this(analyses, [])
    {
    }

    public int CandidateCount => Analyses.Count;
}
