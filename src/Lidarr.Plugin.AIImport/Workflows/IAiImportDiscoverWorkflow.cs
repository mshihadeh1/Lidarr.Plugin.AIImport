using Lidarr.Plugin.AIImport.Configuration;
using Lidarr.Plugin.AIImport.Discovery;
using System.Text.Json.Serialization;

namespace Lidarr.Plugin.AIImport.Workflows;

public interface IAiImportDiscoverWorkflow
{
    AiImportDiscoverResult Discover(
        AiImportSettings settings,
        Action<AiImportJobProgress>? progress = null,
        Func<bool>? stopRequested = null);
}

public sealed class AiImportStopRequestedException : OperationCanceledException
{
    public AiImportStopRequestedException()
        : base("AI Import was stopped before starting another operation.")
    {
    }
}

public static class AiImportMutationAdmission
{
    private static readonly object Gate = new();
    private static int _activeOperations;

    public static IDisposable Admit(Func<bool>? stopRequested)
    {
        lock (Gate)
        {
            if (stopRequested?.Invoke() == true) throw new AiImportStopRequestedException();
            _activeOperations++;
            return new Admission();
        }
    }

    public static T PersistStop<T>(Func<T> persist)
    {
        ArgumentNullException.ThrowIfNull(persist);
        lock (Gate) return persist();
    }

    private sealed class Admission : IDisposable
    {
        private bool _disposed;
        public void Dispose()
        {
            lock (Gate)
            {
                if (_disposed) return;
                _disposed = true;
                _activeOperations--;
            }
        }
    }
}

public sealed record AiImportJobProgress(
    string Stage,
    string Message,
    int OverallPercent,
    string? CurrentAlbum = null,
    int? AlbumPercent = null,
    int CompletedFiles = 0,
    int TotalFiles = 0,
    int CompletedAlbums = 0,
    DateTimeOffset UpdatedAt = default);

[method: JsonConstructor]
public sealed record AiImportDiscoverResult(
    IReadOnlyList<AiImportAnalysisDto> Analyses,
    IReadOnlyList<AiImportDiscoveryDiagnostic> Diagnostics,
    bool YoloMode = false,
    bool YoloContinuation = false,
    bool UnresolvedMutation = false)
{
    public AiImportDiscoverResult(IReadOnlyList<AiImportAnalysisDto> analyses)
        : this(analyses, [])
    {
    }

    public int CandidateCount => Analyses.Count;
}
