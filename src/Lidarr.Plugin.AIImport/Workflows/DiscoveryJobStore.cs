using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text.Json;
using Lidarr.Plugin.AIImport.Commands;
using Lidarr.Plugin.AIImport.Configuration;
using NzbDrone.Common.EnvironmentInfo;
using NzbDrone.Core.Messaging.Commands;

namespace Lidarr.Plugin.AIImport.Workflows;

public enum DiscoveryJobState
{
    Queued,
    Running,
    Succeeded,
    Failed,
    Indeterminate
}

public sealed record DiscoveryJobRecord(
    string JobId,
    int ProviderDefinitionId,
    int? CommandId,
    [property: System.Text.Json.Serialization.JsonConverter(typeof(System.Text.Json.Serialization.JsonStringEnumConverter))]
    DiscoveryJobState State,
    DateTimeOffset CreatedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt,
    AiImportDiscoverResult? Result,
    string? Error);

public sealed record DiscoveryJobCreateResult(DiscoveryJobRecord Job, bool Created);

public sealed class DiscoveryJobStore
{
    private const int TerminalHistoryLimit = 10;
    private static readonly ConcurrentDictionary<string, object> FileLocks = new(StringComparer.Ordinal);
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };
    private readonly string _path;
    private readonly TimeProvider _timeProvider;
    private readonly object _gate;

    public DiscoveryJobStore(IAppFolderInfo appFolderInfo, TimeProvider? timeProvider = null)
        : this(new StandaloneStatePaths(appFolderInfo).DiscoveryJobStore, timeProvider)
    {
    }

    public DiscoveryJobStore(string path, TimeProvider? timeProvider = null)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("Discovery job store path is required.", nameof(path));
        _path = Path.GetFullPath(path);
        _timeProvider = timeProvider ?? TimeProvider.System;
        _gate = FileLocks.GetOrAdd(_path, _ => new object());
    }

    public DiscoveryJobCreateResult CreateOrGetActive(int providerDefinitionId)
    {
        if (providerDefinitionId <= 0) throw new ArgumentOutOfRangeException(nameof(providerDefinitionId));
        lock (_gate)
        {
            using var transaction = AcquireFileLock();
            var jobs = ReadLocked();
            var active = jobs
                .Where(job => job.ProviderDefinitionId == providerDefinitionId && IsActive(job.State))
                .OrderByDescending(job => job.CreatedAt)
                .FirstOrDefault();
            if (active is not null) return new DiscoveryJobCreateResult(active, false);

            var created = new DiscoveryJobRecord(
                Guid.NewGuid().ToString("N"), providerDefinitionId, null, DiscoveryJobState.Queued,
                _timeProvider.GetUtcNow(), null, null, null, null);
            jobs.Add(created);
            Trim(jobs);
            WriteLocked(jobs);
            return new DiscoveryJobCreateResult(created, true);
        }
    }

    public DiscoveryJobRecord AttachCommand(string jobId, int commandId) =>
        Update(jobId, job =>
        {
            if (commandId <= 0) throw new ArgumentOutOfRangeException(nameof(commandId));
            if (job.CommandId is not null && job.CommandId != commandId)
                throw new InvalidDataException("Discovery job is already bound to another command.");
            return job with { CommandId = commandId };
        });

    public DiscoveryJobRecord MarkRunning(string jobId) =>
        Update(jobId, job => IsTerminal(job.State) ? job : job with
        {
            State = DiscoveryJobState.Running,
            StartedAt = job.StartedAt ?? _timeProvider.GetUtcNow(),
            Error = null
        });

    public DiscoveryJobRecord Complete(string jobId, AiImportDiscoverResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return Update(jobId, job => IsTerminal(job.State) ? job : job with
        {
            State = DiscoveryJobState.Succeeded,
            StartedAt = job.StartedAt ?? _timeProvider.GetUtcNow(),
            CompletedAt = _timeProvider.GetUtcNow(),
            Result = result,
            Error = null
        });
    }

    public DiscoveryJobRecord MarkFailed(string jobId, string error) =>
        Finish(jobId, DiscoveryJobState.Failed, error);

    public DiscoveryJobRecord MarkIndeterminate(string jobId, string error) =>
        Finish(jobId, DiscoveryJobState.Indeterminate, error);

    public DiscoveryJobRecord? GetLatest(int providerDefinitionId)
    {
        if (providerDefinitionId <= 0) throw new ArgumentOutOfRangeException(nameof(providerDefinitionId));
        lock (_gate)
        {
            using var transaction = AcquireFileLock();
            return ReadLocked()
                .Where(job => job.ProviderDefinitionId == providerDefinitionId)
                .OrderByDescending(job => job.CreatedAt)
                .FirstOrDefault();
        }
    }

    private DiscoveryJobRecord Finish(string jobId, DiscoveryJobState state, string error)
    {
        if (string.IsNullOrWhiteSpace(error)) error = "The server-side discovery job did not complete.";
        var safeError = error.Replace('\r', ' ').Replace('\n', ' ').Trim();
        if (safeError.Length > 500) safeError = safeError[..500];
        return Update(jobId, job => IsTerminal(job.State) ? job : job with
        {
            State = state,
            StartedAt = job.StartedAt ?? _timeProvider.GetUtcNow(),
            CompletedAt = _timeProvider.GetUtcNow(),
            Result = null,
            Error = safeError
        });
    }

    private DiscoveryJobRecord Update(string jobId, Func<DiscoveryJobRecord, DiscoveryJobRecord> update)
    {
        if (string.IsNullOrWhiteSpace(jobId)) throw new ArgumentException("Discovery job ID is required.", nameof(jobId));
        lock (_gate)
        {
            using var transaction = AcquireFileLock();
            var jobs = ReadLocked();
            var matches = jobs.Select((job, index) => (job, index))
                .Where(item => string.Equals(item.job.JobId, jobId, StringComparison.Ordinal)).ToArray();
            if (matches.Length != 1) throw new InvalidDataException("Discovery job state is missing or ambiguous.");
            var changed = update(matches[0].job);
            jobs[matches[0].index] = changed;
            Trim(jobs);
            WriteLocked(jobs);
            return changed;
        }
    }

    private List<DiscoveryJobRecord> ReadLocked()
    {
        if (!File.Exists(_path)) return [];
        using var stream = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var jobs = JsonSerializer.Deserialize<List<DiscoveryJobRecord>>(stream, Options)
            ?? throw new InvalidDataException("The discovery job store is empty or malformed.");
        Validate(jobs);
        return jobs;
    }

    private FileStream AcquireFileLock()
    {
        var directory = Path.GetDirectoryName(_path) ?? throw new InvalidOperationException("Discovery job store has no parent directory.");
        Directory.CreateDirectory(directory);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var lockPath = _path + ".lock";
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                var stream = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                if (!OperatingSystem.IsWindows())
                    File.SetUnixFileMode(lockPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                return stream;
            }
            catch (IOException) when (attempt < 200)
            {
                Thread.Sleep(25);
            }
        }
    }

    private void WriteLocked(IReadOnlyList<DiscoveryJobRecord> jobs)
    {
        Validate(jobs);
        var directory = Path.GetDirectoryName(_path) ?? throw new InvalidOperationException("Discovery job store has no parent directory.");
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
                JsonSerializer.Serialize(stream, jobs, Options);
                stream.Flush(true);
            }
            File.Move(temporary, _path, true);
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(_path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            FlushDirectory(directory);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static void Validate(IReadOnlyList<DiscoveryJobRecord?> jobs)
    {
        if (jobs.Any(job => job is null) ||
            jobs.Where(job => job is not null).Select(job => job!.JobId).Distinct(StringComparer.Ordinal).Count() != jobs.Count ||
            jobs.Any(job => job is null || string.IsNullOrWhiteSpace(job.JobId) || job.ProviderDefinitionId <= 0 ||
                            job.JobId.Length != 32 || !job.JobId.All(Uri.IsHexDigit) ||
                            !Enum.IsDefined(job.State) ||
                            job.CreatedAt == default ||
                            (job.StartedAt is not null && job.StartedAt < job.CreatedAt) ||
                            (job.CompletedAt is not null && job.CompletedAt < (job.StartedAt ?? job.CreatedAt)) ||
                            (IsTerminal(job.State) != (job.CompletedAt is not null)) ||
                            (job.State == DiscoveryJobState.Succeeded && job.Result is null) ||
                            (job.State != DiscoveryJobState.Succeeded && job.Result is not null)))
            throw new InvalidDataException("Discovery job state is malformed.");
    }

    private static bool IsActive(DiscoveryJobState state) =>
        state is DiscoveryJobState.Queued or DiscoveryJobState.Running;

    private static bool IsTerminal(DiscoveryJobState state) =>
        state is DiscoveryJobState.Succeeded or DiscoveryJobState.Failed or DiscoveryJobState.Indeterminate;

    private static void Trim(List<DiscoveryJobRecord> jobs)
    {
        var active = jobs.Where(job => IsActive(job.State)).Select(job => job.JobId);
        var terminal = jobs.Where(job => IsTerminal(job.State))
            .OrderByDescending(job => job.CreatedAt).Take(TerminalHistoryLimit).Select(job => job.JobId);
        var keep = active.Concat(terminal).ToHashSet(StringComparer.Ordinal);
        jobs.RemoveAll(job => !keep.Contains(job.JobId));
    }

    private static void FlushDirectory(string directory)
    {
        if (!OperatingSystem.IsLinux()) return;
        const int directoryFlag = 0x10000;
        const int closeOnExec = 0x80000;
        var descriptor = open(directory, directoryFlag | closeOnExec);
        if (descriptor < 0) throw new IOException("Unable to open the discovery-job directory for durability.");
        try
        {
            if (fsync(descriptor) != 0) throw new IOException("Unable to durably flush the discovery-job directory.");
        }
        finally
        {
            close(descriptor);
        }
    }

    [DllImport("libc", SetLastError = true)] private static extern int open(string path, int flags);
    [DllImport("libc", SetLastError = true)] private static extern int fsync(int descriptor);
    [DllImport("libc", SetLastError = true)] private static extern int close(int descriptor);
}

public interface IAiImportDiscoveryJobService
{
    DiscoveryJobRecord Start(int providerDefinitionId);
    DiscoveryJobRecord RestartAfterIndeterminate(int providerDefinitionId);
    DiscoveryJobRecord? GetLatest(int providerDefinitionId);
}

public sealed class AiImportDiscoveryJobService : IAiImportDiscoveryJobService
{
    private static readonly object AdmissionGate = new();
    private readonly DiscoveryJobStore _store;
    private readonly IDiscoveryCommandSubmitter _submitter;
    private readonly IAiImportSettingsResolver _settingsResolver;

    public AiImportDiscoveryJobService(
        DiscoveryJobStore store,
        IDiscoveryCommandSubmitter submitter,
        IAiImportSettingsResolver settingsResolver)
    {
        _store = store;
        _submitter = submitter;
        _settingsResolver = settingsResolver;
    }

    public DiscoveryJobRecord Start(int providerDefinitionId)
    {
        lock (AdmissionGate)
        {
            _settingsResolver.ResolveEnabled(providerDefinitionId);
            var latest = Reconcile(_store.GetLatest(providerDefinitionId));
            if (latest is { State: DiscoveryJobState.Indeterminate }) return latest;
            return StartNew(providerDefinitionId);
        }
    }

    public DiscoveryJobRecord RestartAfterIndeterminate(int providerDefinitionId)
    {
        lock (AdmissionGate)
        {
            _settingsResolver.ResolveEnabled(providerDefinitionId);
            var latest = Reconcile(_store.GetLatest(providerDefinitionId));
            if (latest is { State: DiscoveryJobState.Queued or DiscoveryJobState.Running }) return latest;
            if (latest is not { State: DiscoveryJobState.Indeterminate })
                throw new InvalidOperationException("There is no indeterminate discovery job to acknowledge.");
            return StartNew(providerDefinitionId);
        }
    }

    public DiscoveryJobRecord? GetLatest(int providerDefinitionId)
    {
        lock (AdmissionGate)
        {
            _settingsResolver.ResolveEnabled(providerDefinitionId);
            return Reconcile(_store.GetLatest(providerDefinitionId));
        }
    }

    private DiscoveryJobRecord StartNew(int providerDefinitionId)
    {
        var selected = _store.CreateOrGetActive(providerDefinitionId);
        if (!selected.Created) return Reconcile(selected.Job)!;
        try
        {
            var commandId = _submitter.Submit(new AiImportDiscoverCommand
            {
                ProviderDefinitionId = providerDefinitionId,
                JobId = selected.Job.JobId
            });
            return _store.AttachCommand(selected.Job.JobId, commandId);
        }
        catch (Exception exception)
        {
            _store.MarkIndeterminate(selected.Job.JobId,
                $"Discovery command submission did not return a durable receipt: {exception.Message}");
            throw;
        }
    }

    private DiscoveryJobRecord? Reconcile(DiscoveryJobRecord? job)
    {
        if (job is null || job.State is not (DiscoveryJobState.Queued or DiscoveryJobState.Running)) return job;
        var command = job.CommandId is { } commandId
            ? _submitter.Get(commandId)
            : _submitter.Find(job.JobId);
        if (command is null)
        {
            return _store.MarkIndeterminate(job.JobId,
                job.CommandId is null
                    ? "The persisted discovery job has no durable Lidarr command receipt; submission or execution may have occurred and automatic retry is forbidden."
                    : "The Lidarr discovery command receipt no longer resolves; automatic retry is forbidden.");
        }
        if (job.CommandId is null) job = _store.AttachCommand(job.JobId, command.Id);
        return command.Status switch
        {
            CommandStatus.Queued => job,
            CommandStatus.Started => _store.MarkRunning(job.JobId),
            CommandStatus.Completed => _store.MarkIndeterminate(job.JobId,
                "Lidarr completed the command without a persisted discovery result; automatic retry is forbidden."),
            _ => _store.MarkIndeterminate(job.JobId,
                $"Lidarr command ended as {command.Status}; automatic retry is forbidden.")
        };
    }
}
