using Lidarr.Plugin.AIImport.Configuration;
using Lidarr.Plugin.AIImport.Discovery;

namespace Lidarr.Plugin.AIImport.Workflows;

public interface IAiImportDiscoverWorkflow
{
    AiImportDiscoverResult Discover(AiImportSettings settings);
}

public sealed record AiImportDiscoverResult(IReadOnlyList<AiImportAnalysisDto> Analyses)
{
    public int CandidateCount => Analyses.Count;
}
