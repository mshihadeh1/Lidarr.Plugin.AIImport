using System.Text.Json;
using Lidarr.Plugin.AIImport.Approval;
using Lidarr.Plugin.AIImport.Matching;
using NzbDrone.Common.EnvironmentInfo;

namespace Lidarr.Plugin.AIImport.Workflows;

public sealed record StoredStandaloneAnalysis(string AnalysisId, string DownloadId, MatchProposal Proposal);

public sealed class StandaloneAnalysisStore
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };
    private readonly string _path;
    private readonly object _gate = new();

    public StandaloneAnalysisStore(IAppFolderInfo appFolderInfo)
        : this(new StandaloneStatePaths(appFolderInfo).AnalysisStore)
    {
    }

    public StandaloneAnalysisStore(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("Analysis store path is required.", nameof(path));
        _path = Path.GetFullPath(path);
    }

    public void Replace(IReadOnlyList<StoredStandaloneAnalysis> analyses)
    {
        ArgumentNullException.ThrowIfNull(analyses);
        if (analyses.Select(x => x.AnalysisId).Distinct(StringComparer.Ordinal).Count() != analyses.Count ||
            analyses.Any(x => string.IsNullOrWhiteSpace(x.AnalysisId) || string.IsNullOrWhiteSpace(x.DownloadId)))
            throw new InvalidDataException("Standalone analyses must have unique, complete identities.");
        lock (_gate)
        {
            var directory = Path.GetDirectoryName(_path) ?? throw new InvalidOperationException("Analysis store has no parent directory.");
            Directory.CreateDirectory(directory);
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            var temporary = Path.Combine(directory, "." + Path.GetFileName(_path) + "." + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
                {
                    if (!OperatingSystem.IsWindows())
                        File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                    JsonSerializer.Serialize(stream, analyses, Options);
                    stream.Flush(true);
                }
                File.Move(temporary, _path, true);
            }
            finally
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
        }
    }

    public StoredStandaloneAnalysis GetRequired(string analysisId)
    {
        if (string.IsNullOrWhiteSpace(analysisId)) throw new ArgumentException("Analysis ID is required.", nameof(analysisId));
        lock (_gate)
        {
            if (!File.Exists(_path))
                throw new StaleApprovalException("The analysis is missing or stale; refresh discovery before approval.");
            using var stream = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var analyses = JsonSerializer.Deserialize<List<StoredStandaloneAnalysis>>(stream, Options)
                ?? throw new InvalidDataException("The standalone analysis store is empty or malformed.");
            var matches = analyses.Where(x => string.Equals(x.AnalysisId, analysisId, StringComparison.Ordinal)).ToArray();
            return matches.Length == 1
                ? matches[0]
                : throw new StaleApprovalException("The analysis is missing or stale; refresh discovery before approval.");
        }
    }
}
