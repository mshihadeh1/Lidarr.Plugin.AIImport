using System.Text.Json;
using Lidarr.Plugin.AIImport.Approval;
using Lidarr.Plugin.AIImport.Matching;
using NzbDrone.Common.EnvironmentInfo;

namespace Lidarr.Plugin.AIImport.Workflows;

public sealed record StoredStandaloneAnalysis(
    string AnalysisId,
    string DownloadId,
    MatchProposal Proposal,
    bool HumanOverride = false,
    string? ParentAnalysisId = null,
    DateTimeOffset? ExpiresAt = null,
    string? FolderPath = null);

public sealed class StandaloneAnalysisStore
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };
    private readonly string _path;
    private readonly TimeProvider _timeProvider;
    private readonly object _gate = new();

    public StandaloneAnalysisStore(IAppFolderInfo appFolderInfo, TimeProvider? timeProvider = null)
        : this(new StandaloneStatePaths(appFolderInfo).AnalysisStore, timeProvider)
    {
    }

    public StandaloneAnalysisStore(string path, TimeProvider? timeProvider = null)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("Analysis store path is required.", nameof(path));
        _path = Path.GetFullPath(path);
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public void Replace(IReadOnlyList<StoredStandaloneAnalysis> analyses)
    {
        ArgumentNullException.ThrowIfNull(analyses);
        Validate(analyses);
        lock (_gate)
        {
            var merged = analyses.ToList();
            if (File.Exists(_path))
            {
                var currentIds = merged.Select(x => x.AnalysisId).ToHashSet(StringComparer.Ordinal);
                merged.AddRange(ReadLocked().Where(x =>
                    x.HumanOverride && x.ExpiresAt > _timeProvider.GetUtcNow() && !currentIds.Contains(x.AnalysisId)));
            }
            Validate(merged);
            WriteLocked(merged);
        }
    }

    public void Upsert(StoredStandaloneAnalysis analysis)
    {
        ArgumentNullException.ThrowIfNull(analysis);
        Validate([analysis]);
        lock (_gate)
        {
            var analyses = ReadLocked();
            analyses.RemoveAll(x => string.Equals(x.AnalysisId, analysis.AnalysisId, StringComparison.Ordinal));
            analyses.Add(analysis);
            Validate(analyses);
            WriteLocked(analyses);
        }
    }

    public StoredStandaloneAnalysis GetRequired(string analysisId)
    {
        if (string.IsNullOrWhiteSpace(analysisId)) throw new ArgumentException("Analysis ID is required.", nameof(analysisId));
        lock (_gate)
        {
            if (!File.Exists(_path))
                throw new StaleApprovalException("The analysis is missing or stale; refresh discovery before approval.");
            var analyses = ReadLocked();
            var matches = analyses.Where(x => string.Equals(x.AnalysisId, analysisId, StringComparison.Ordinal)).ToArray();
            if (matches.Length != 1)
                throw new StaleApprovalException("The analysis is missing or stale; refresh discovery before approval.");
            var stored = matches[0];
            if (stored.HumanOverride && stored.ExpiresAt <= _timeProvider.GetUtcNow())
                throw new StaleApprovalException("The human-reviewed mapping has expired. Run discovery and review it again.");
            if (stored.HumanOverride)
            {
                var parents = analyses.Where(x =>
                    !x.HumanOverride &&
                    string.Equals(x.AnalysisId, stored.ParentAnalysisId, StringComparison.Ordinal) &&
                    string.Equals(x.DownloadId, stored.DownloadId, StringComparison.Ordinal) &&
                    x.Proposal.Status == ProposalStatus.ManualReview).ToArray();
                if (parents.Length != 1)
                    throw new StaleApprovalException("The human-reviewed mapping no longer has valid manual-review provenance. Run discovery and review it again.");
            }
            return stored;
        }
    }

    private static void Validate(IReadOnlyList<StoredStandaloneAnalysis> analyses)
    {
        if (analyses.Select(x => x.AnalysisId).Distinct(StringComparer.Ordinal).Count() != analyses.Count ||
            analyses.Any(x => string.IsNullOrWhiteSpace(x.AnalysisId) ||
                (string.IsNullOrWhiteSpace(x.DownloadId) && string.IsNullOrWhiteSpace(x.FolderPath)) ||
                (x.HumanOverride && (string.IsNullOrWhiteSpace(x.ParentAnalysisId) || x.ExpiresAt is null))))
            throw new InvalidDataException("Standalone analyses must have unique, complete identities.");
    }

    private List<StoredStandaloneAnalysis> ReadLocked()
    {
        using var stream = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return JsonSerializer.Deserialize<List<StoredStandaloneAnalysis>>(stream, Options)
            ?? throw new InvalidDataException("The standalone analysis store is empty or malformed.");
    }

    private void WriteLocked(IReadOnlyList<StoredStandaloneAnalysis> analyses)
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
