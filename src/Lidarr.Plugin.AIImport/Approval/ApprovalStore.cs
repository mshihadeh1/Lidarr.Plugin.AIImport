using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Runtime.InteropServices;
using Lidarr.Plugin.AIImport.Workflows;
using NzbDrone.Common.EnvironmentInfo;

namespace Lidarr.Plugin.AIImport.Approval;

public enum ApprovalState
{
    Approved,
    Executing,
    Succeeded,
    Failed,
    Indeterminate,
    RollbackExecuting,
    RolledBack,
    RollbackIndeterminate
}

public sealed record CreatedApproval(string ApprovalId, string Token, DateTimeOffset ExpiresAt);

public sealed record ApprovalAuthorizationBinding(
    string AuthorizationId,
    string SourceId,
    int TrackId,
    string SourcePath,
    ulong SourceDevice,
    ulong SourceInode);

public sealed record ReconciledImportFile(
    int HistoryId,
    string AuthorizationId,
    string SourceId,
    int TrackFileId,
    int TrackId,
    string SourcePath,
    ulong SourceDevice,
    ulong SourceInode,
    string ImportedPath,
    long Size,
    JsonObject Quality,
    int IndexerFlags,
    ulong DestinationDevice,
    ulong DestinationInode);

public sealed record ApprovalRecord(
    string ApprovalId,
    string AnalysisId,
    string TokenHash,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt,
    ApprovalState State,
    JsonElement Payload,
    DateTimeOffset? ExecutionStartedAt,
    DateTimeOffset? CompletedAt,
    string? Outcome,
    IReadOnlyList<string>? AuthorizationIds = null,
    int? CommandId = null,
    string? CommandStatus = null,
    IReadOnlyList<ApprovalAuthorizationBinding>? AuthorizationBindings = null,
    IReadOnlyList<ReconciledImportFile>? ReconciledFiles = null,
    DateTimeOffset? RolledBackAt = null,
    DateTimeOffset? ActivityCleanupStartedAt = null,
    DateTimeOffset? ActivityCleanupCompletedAt = null,
    string? ActivityCleanupOutcome = null);

public sealed class ApprovalAuthenticationException(string message) : Exception(message);
public sealed class ApprovalReplayException(string message) : Exception(message);
public sealed class ApprovalExpiredException(string message) : Exception(message);
public sealed class StaleApprovalException(string message) : Exception(message);

public sealed class ApprovalStore
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> FileLocks = new(StringComparer.Ordinal);
    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = true };
    private readonly string _path;
    private readonly TimeProvider _timeProvider;
    private readonly SemaphoreSlim _lock;

    public ApprovalStore(IAppFolderInfo appFolderInfo)
        : this(new StandaloneStatePaths(appFolderInfo).ApprovalStore)
    {
    }

    public ApprovalStore(string path, TimeProvider? timeProvider = null)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("Approval store path is required.", nameof(path));
        _path = Path.GetFullPath(path);
        _timeProvider = timeProvider ?? TimeProvider.System;
        _lock = FileLocks.GetOrAdd(_path, _ => new SemaphoreSlim(1, 1));
    }

    public async Task<CreatedApproval> CreateAsync(
        string analysisId,
        JsonElement payload,
        TimeSpan lifetime,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(analysisId)) throw new ArgumentException("Analysis ID is required.", nameof(analysisId));
        if (lifetime <= TimeSpan.Zero || lifetime > TimeSpan.FromHours(24))
            throw new ArgumentOutOfRangeException(nameof(lifetime), "Approval lifetime must be between zero and 24 hours.");
        var now = _timeProvider.GetUtcNow();
        var tokenBytes = RandomNumberGenerator.GetBytes(32);
        var token = Base64Url(tokenBytes);
        var id = Base64Url(RandomNumberGenerator.GetBytes(18));
        var record = new ApprovalRecord(
            id, analysisId, HashToken(token), now, now.Add(lifetime), ApprovalState.Approved,
            payload.Clone(), null, null, null);

        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var processLock = await AcquireProcessLockAsync(cancellationToken).ConfigureAwait(false);
            var records = await ReadAsync(cancellationToken).ConfigureAwait(false);
            records.Add(record);
            await WriteAsync(records, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _lock.Release();
        }
        return new CreatedApproval(id, token, record.ExpiresAt);
    }

    public async Task<ApprovalRecord> AuthenticateAsync(
        string approvalId,
        string token,
        CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var processLock = await AcquireProcessLockAsync(cancellationToken).ConfigureAwait(false);
            var record = (await ReadAsync(cancellationToken).ConfigureAwait(false))
                .SingleOrDefault(x => string.Equals(x.ApprovalId, approvalId, StringComparison.Ordinal));
            if (record is null || !TokenMatches(record, token))
                throw new ApprovalAuthenticationException("Approval credentials are invalid.");
            if (record.State != ApprovalState.Approved)
                throw new ApprovalReplayException($"Approval is already {record.State.ToString().ToLowerInvariant()}.");
            if (_timeProvider.GetUtcNow() >= record.ExpiresAt)
                throw new ApprovalExpiredException("Approval has expired.");
            return record;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<ApprovalRecord> AuthenticateTrustedAsync(string approvalId, CancellationToken cancellationToken)
    {
        var record = await GetAsync(approvalId, cancellationToken).ConfigureAwait(false)
            ?? throw new ApprovalAuthenticationException("Approval identity is invalid.");
        if (record.State != ApprovalState.Approved)
            throw new ApprovalReplayException($"Approval is already {record.State.ToString().ToLowerInvariant()}.");
        if (_timeProvider.GetUtcNow() >= record.ExpiresAt) throw new ApprovalExpiredException("Approval has expired.");
        return record;
    }

    public async Task<ApprovalRecord> AuthenticateForStatusTrustedAsync(string approvalId, CancellationToken cancellationToken) =>
        await GetAsync(approvalId, cancellationToken).ConfigureAwait(false)
        ?? throw new ApprovalAuthenticationException("Approval identity is invalid.");

    public async Task<ApprovalRecord> AuthenticateForReconciliationTrustedAsync(string approvalId, CancellationToken cancellationToken)
    {
        var record = await AuthenticateForStatusTrustedAsync(approvalId, cancellationToken).ConfigureAwait(false);
        if (record.State is not (ApprovalState.Executing or ApprovalState.Indeterminate))
            throw new ApprovalReplayException("Only executing or indeterminate approvals can be reconciled.");
        return record;
    }

    public async Task<ApprovalRecord> AuthenticateForStatusAsync(
        string approvalId,
        string token,
        CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var processLock = await AcquireProcessLockAsync(cancellationToken).ConfigureAwait(false);
            var record = (await ReadAsync(cancellationToken).ConfigureAwait(false))
                .SingleOrDefault(x => string.Equals(x.ApprovalId, approvalId, StringComparison.Ordinal));
            if (record is null || !TokenMatches(record, token))
                throw new ApprovalAuthenticationException("Approval credentials are invalid.");
            return record;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<ApprovalRecord> AuthenticateForReconciliationAsync(
        string approvalId,
        string token,
        CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var processLock = await AcquireProcessLockAsync(cancellationToken).ConfigureAwait(false);
            var record = (await ReadAsync(cancellationToken).ConfigureAwait(false))
                .SingleOrDefault(x => string.Equals(x.ApprovalId, approvalId, StringComparison.Ordinal));
            if (record is null || !TokenMatches(record, token))
                throw new ApprovalAuthenticationException("Approval credentials are invalid.");
            if (record.State is not (ApprovalState.Executing or ApprovalState.Indeterminate))
                throw new ApprovalReplayException("Only executing or indeterminate approvals can be reconciled.");
            return record;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<ApprovalRecord> BeginExecutionAsync(
        string approvalId,
        string token,
        string currentAnalysisId,
        CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var processLock = await AcquireProcessLockAsync(cancellationToken).ConfigureAwait(false);
            var records = await ReadAsync(cancellationToken).ConfigureAwait(false);
            var index = records.FindIndex(x => string.Equals(x.ApprovalId, approvalId, StringComparison.Ordinal));
            if (index < 0) throw new ApprovalAuthenticationException("Approval credentials are invalid.");
            var record = records[index];
            if (!CryptographicOperations.FixedTimeEquals(
                    Convert.FromHexString(record.TokenHash),
                    Convert.FromHexString(HashToken(token))))
                throw new ApprovalAuthenticationException("Approval credentials are invalid.");
            if (record.State != ApprovalState.Approved)
                throw new ApprovalReplayException($"Approval is already {record.State.ToString().ToLowerInvariant()}.");
            if (_timeProvider.GetUtcNow() >= record.ExpiresAt)
                throw new ApprovalExpiredException("Approval has expired.");
            if (!string.Equals(record.AnalysisId, currentAnalysisId, StringComparison.Ordinal))
                throw new StaleApprovalException("The approved analysis no longer matches current Lidarr and source state.");

            record = record with { State = ApprovalState.Executing, ExecutionStartedAt = _timeProvider.GetUtcNow() };
            records[index] = record;
            await WriteAsync(records, cancellationToken).ConfigureAwait(false);
            return record;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<ApprovalRecord> BeginExecutionTrustedAsync(string approvalId, string currentAnalysisId, CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var processLock = await AcquireProcessLockAsync(cancellationToken).ConfigureAwait(false);
            var records = await ReadAsync(cancellationToken).ConfigureAwait(false);
            var index = records.FindIndex(x => string.Equals(x.ApprovalId, approvalId, StringComparison.Ordinal));
            if (index < 0) throw new ApprovalAuthenticationException("Approval identity is invalid.");
            var record = records[index];
            if (record.State != ApprovalState.Approved) throw new ApprovalReplayException($"Approval is already {record.State.ToString().ToLowerInvariant()}.");
            if (_timeProvider.GetUtcNow() >= record.ExpiresAt) throw new ApprovalExpiredException("Approval has expired.");
            if (!string.Equals(record.AnalysisId, currentAnalysisId, StringComparison.Ordinal)) throw new StaleApprovalException("The approved analysis no longer matches current state.");
            record = record with { State = ApprovalState.Executing, ExecutionStartedAt = _timeProvider.GetUtcNow() };
            records[index] = record;
            await WriteAsync(records, cancellationToken).ConfigureAwait(false);
            return record;
        }
        finally { _lock.Release(); }
    }

    public async Task CompleteAsync(
        string approvalId,
        ApprovalState state,
        string outcome,
        CancellationToken cancellationToken)
    {
        if (state is not (ApprovalState.Succeeded or ApprovalState.Failed or ApprovalState.Indeterminate))
            throw new ArgumentOutOfRangeException(nameof(state));
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var processLock = await AcquireProcessLockAsync(cancellationToken).ConfigureAwait(false);
            var records = await ReadAsync(cancellationToken).ConfigureAwait(false);
            var index = records.FindIndex(x => string.Equals(x.ApprovalId, approvalId, StringComparison.Ordinal));
            if (index < 0) throw new KeyNotFoundException("Approval was not found.");
            var record = records[index];
            if (record.State != ApprovalState.Executing)
                throw new ApprovalReplayException("Only an executing approval may be completed.");
            records[index] = record with
            {
                State = state,
                CompletedAt = _timeProvider.GetUtcNow(),
                Outcome = outcome
            };
            await WriteAsync(records, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task CompleteVerifiedReconciliationAsync(
        string approvalId,
        string outcome,
        IReadOnlyList<ReconciledImportFile> reconciledFiles,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(outcome)) throw new ArgumentException("Outcome is required.", nameof(outcome));
        ArgumentNullException.ThrowIfNull(reconciledFiles);
        if (reconciledFiles.Count == 0 || reconciledFiles.Any(file =>
                                        file.HistoryId <= 0 || string.IsNullOrWhiteSpace(file.AuthorizationId) ||
                                        string.IsNullOrWhiteSpace(file.SourceId) || file.TrackFileId <= 0 || file.TrackId <= 0 ||
                                        string.IsNullOrWhiteSpace(file.SourcePath) || !Path.IsPathFullyQualified(file.SourcePath) ||
                                        string.IsNullOrWhiteSpace(file.ImportedPath) || !Path.IsPathFullyQualified(file.ImportedPath) ||
                                        file.Size < 0 || file.Quality is null || file.SourceInode == 0 || file.DestinationInode == 0) ||
            reconciledFiles.Select(file => file.HistoryId).Distinct().Count() != reconciledFiles.Count ||
            reconciledFiles.Select(file => file.AuthorizationId).Distinct(StringComparer.Ordinal).Count() != reconciledFiles.Count ||
            reconciledFiles.Select(file => file.SourceId).Distinct(StringComparer.Ordinal).Count() != reconciledFiles.Count ||
            reconciledFiles.Select(file => file.TrackFileId).Distinct().Count() != reconciledFiles.Count ||
            reconciledFiles.Select(file => file.TrackId).Distinct().Count() != reconciledFiles.Count ||
            reconciledFiles.Select(file => file.ImportedPath).Distinct(StringComparer.Ordinal).Count() != reconciledFiles.Count)
            throw new ArgumentException("Reconciled files must be complete and unique.", nameof(reconciledFiles));
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var processLock = await AcquireProcessLockAsync(cancellationToken).ConfigureAwait(false);
            var records = await ReadAsync(cancellationToken).ConfigureAwait(false);
            var index = records.FindIndex(x => string.Equals(x.ApprovalId, approvalId, StringComparison.Ordinal));
            if (index < 0) throw new KeyNotFoundException("Approval was not found.");
            var record = records[index];
            if (record.State is not (ApprovalState.Executing or ApprovalState.Indeterminate))
                throw new ApprovalReplayException("Only executing or indeterminate approvals can complete reconciliation.");
            var plan = record.Payload.Deserialize<ApprovedImportPlan>()
                ?? throw new InvalidDataException("Approved import plan is malformed.");
            var bindings = record.AuthorizationBindings ?? throw new InvalidDataException("Authorization evidence is missing.");
            var otherEvidence = records
                .Where(item => !string.Equals(item.ApprovalId, approvalId, StringComparison.Ordinal))
                .SelectMany(item => item.ReconciledFiles ?? Array.Empty<ReconciledImportFile>())
                .ToArray();
            if (reconciledFiles.Any(item => otherEvidence.Any(other =>
                    other.TrackFileId == item.TrackFileId ||
                    string.Equals(other.ImportedPath, item.ImportedPath, StringComparison.Ordinal))))
                throw new InvalidDataException("Reconciliation evidence conflicts with another import audit record.");
            foreach (var evidence in reconciledFiles)
            {
                var approved = plan.Files.SingleOrDefault(file =>
                    string.Equals(file.SourceId, evidence.SourceId, StringComparison.Ordinal));
                var binding = bindings.SingleOrDefault(item =>
                    string.Equals(item.AuthorizationId, evidence.AuthorizationId, StringComparison.Ordinal));
                if (approved is null || binding is null ||
                    approved.TrackId != evidence.TrackId ||
                    !string.Equals(approved.SourcePath, evidence.SourcePath, StringComparison.Ordinal) ||
                    approved.Identity.Device != evidence.SourceDevice || approved.Identity.Inode != evidence.SourceInode ||
                    approved.Size != evidence.Size || approved.IndexerFlags != evidence.IndexerFlags ||
                    !JsonNode.DeepEquals(approved.Quality, evidence.Quality) ||
                    binding.SourceId != evidence.SourceId || binding.TrackId != evidence.TrackId ||
                    !string.Equals(binding.SourcePath, evidence.SourcePath, StringComparison.Ordinal) ||
                    binding.SourceDevice != evidence.SourceDevice || binding.SourceInode != evidence.SourceInode ||
                    evidence.DestinationDevice != evidence.SourceDevice || evidence.DestinationInode != evidence.SourceInode)
                    throw new InvalidDataException("Reconciliation evidence does not exactly match its approved file and authorization binding.");
            }
            if (reconciledFiles.Count != plan.Files.Count || bindings.Count != plan.Files.Count ||
                !reconciledFiles.Select(x => x.SourceId).ToHashSet(StringComparer.Ordinal)
                    .SetEquals(plan.Files.Select(x => x.SourceId)) ||
                !reconciledFiles.Select(x => x.TrackId).ToHashSet().SetEquals(plan.Files.Select(x => x.TrackId)) ||
                !reconciledFiles.Select(x => x.AuthorizationId).ToHashSet(StringComparer.Ordinal)
                    .SetEquals(bindings.Select(x => x.AuthorizationId)))
                throw new InvalidDataException("Reconciliation evidence does not map one-to-one to the approved plan.");
            records[index] = record with
            {
                State = ApprovalState.Succeeded,
                CompletedAt = _timeProvider.GetUtcNow(),
                Outcome = outcome,
                ReconciledFiles = reconciledFiles.ToArray()
            };
            await WriteAsync(records, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task RecordAuthorizationsAsync(
        string approvalId,
        IReadOnlyList<string> authorizationIds,
        CancellationToken cancellationToken)
    {
        if (authorizationIds.Count == 0 || authorizationIds.Any(string.IsNullOrWhiteSpace) ||
            authorizationIds.Distinct(StringComparer.Ordinal).Count() != authorizationIds.Count)
            throw new ArgumentException("Authorization IDs must be nonempty and unique.", nameof(authorizationIds));
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var processLock = await AcquireProcessLockAsync(cancellationToken).ConfigureAwait(false);
            var records = await ReadAsync(cancellationToken).ConfigureAwait(false);
            var index = records.FindIndex(x => string.Equals(x.ApprovalId, approvalId, StringComparison.Ordinal));
            if (index < 0) throw new KeyNotFoundException("Approval was not found.");
            var record = records[index];
            if (record.State != ApprovalState.Executing || record.AuthorizationIds is not null)
                throw new ApprovalReplayException("Native authorizations can be recorded exactly once for an executing approval.");
            records[index] = record with { AuthorizationIds = authorizationIds.ToArray() };
            await WriteAsync(records, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task RecordAuthorizationBindingsAsync(
        string approvalId,
        IReadOnlyList<ApprovalAuthorizationBinding> bindings,
        CancellationToken cancellationToken)
    {
        if (bindings.Count == 0 ||
            bindings.Any(binding => string.IsNullOrWhiteSpace(binding.AuthorizationId) ||
                                    string.IsNullOrWhiteSpace(binding.SourceId) ||
                                    binding.TrackId <= 0 ||
                                    string.IsNullOrWhiteSpace(binding.SourcePath) ||
                                    !Path.IsPathFullyQualified(binding.SourcePath) ||
                                    binding.SourceInode == 0) ||
            bindings.Select(binding => binding.AuthorizationId).Distinct(StringComparer.Ordinal).Count() != bindings.Count ||
            bindings.Select(binding => binding.SourceId).Distinct(StringComparer.Ordinal).Count() != bindings.Count)
            throw new ArgumentException("Authorization bindings must be complete and unique.", nameof(bindings));

        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var processLock = await AcquireProcessLockAsync(cancellationToken).ConfigureAwait(false);
            var records = await ReadAsync(cancellationToken).ConfigureAwait(false);
            var index = records.FindIndex(x => string.Equals(x.ApprovalId, approvalId, StringComparison.Ordinal));
            if (index < 0) throw new KeyNotFoundException("Approval was not found.");
            var record = records[index];
            if (record.State != ApprovalState.Executing || record.AuthorizationIds is not null ||
                record.AuthorizationBindings is not null)
                throw new ApprovalReplayException("Native authorization bindings can be recorded exactly once for an executing approval.");
            var snapshot = bindings.ToArray();
            records[index] = record with
            {
                AuthorizationIds = snapshot.Select(binding => binding.AuthorizationId).ToArray(),
                AuthorizationBindings = snapshot
            };
            await WriteAsync(records, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task RecordCommandAsync(
        string approvalId,
        int commandId,
        string commandStatus,
        CancellationToken cancellationToken)
    {
        if (commandId <= 0) throw new ArgumentOutOfRangeException(nameof(commandId));
        if (string.IsNullOrWhiteSpace(commandStatus))
            throw new ArgumentException("Command status is required.", nameof(commandStatus));
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var processLock = await AcquireProcessLockAsync(cancellationToken).ConfigureAwait(false);
            var records = await ReadAsync(cancellationToken).ConfigureAwait(false);
            var index = records.FindIndex(x => string.Equals(x.ApprovalId, approvalId, StringComparison.Ordinal));
            if (index < 0) throw new KeyNotFoundException("Approval was not found.");
            var record = records[index];
            if (record.State != ApprovalState.Executing || record.AuthorizationBindings is null || record.CommandId is not null)
                throw new ApprovalReplayException("A Lidarr command can be recorded exactly once after native authorization bindings.");
            records[index] = record with { CommandId = commandId, CommandStatus = commandStatus };
            await WriteAsync(records, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<int> RecoverInterruptedExecutionsAsync(CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var processLock = await AcquireProcessLockAsync(cancellationToken).ConfigureAwait(false);
            var records = await ReadAsync(cancellationToken).ConfigureAwait(false);
            var recovered = 0;
            for (var index = 0; index < records.Count; index++)
            {
                if (records[index].State == ApprovalState.RollbackExecuting)
                {
                    records[index] = records[index] with
                    {
                        State = ApprovalState.RollbackIndeterminate,
                        Outcome = "Lidarr restart interrupted rollback; reconcile filesystem and database state manually."
                    };
                    recovered++;
                    continue;
                }
                if (records[index].State != ApprovalState.Executing) continue;
                records[index] = records[index] with
                {
                    State = ApprovalState.Indeterminate,
                    CompletedAt = _timeProvider.GetUtcNow(),
                    Outcome = "Lidarr restart interrupted execution; reconcile external state before any new approval."
                };
                recovered++;
            }
            if (recovered != 0) await WriteAsync(records, cancellationToken).ConfigureAwait(false);
            return recovered;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<ApprovalRecord?> GetAsync(string approvalId, CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var processLock = await AcquireProcessLockAsync(cancellationToken).ConfigureAwait(false);
            return (await ReadAsync(cancellationToken).ConfigureAwait(false))
                .SingleOrDefault(x => string.Equals(x.ApprovalId, approvalId, StringComparison.Ordinal));
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<IReadOnlyList<ApprovalRecord>> GetPendingReconciliationAsync(CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var processLock = await AcquireProcessLockAsync(cancellationToken).ConfigureAwait(false);
            return (await ReadAsync(cancellationToken).ConfigureAwait(false))
                .Where(record => record.State is ApprovalState.Executing or ApprovalState.Indeterminate)
                .Where(record => record.AuthorizationBindings is { Count: > 0 })
                .OrderBy(record => record.ExecutionStartedAt)
                .ToArray();
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<IReadOnlyList<ApprovalRecord>> GetReviewHistoryAsync(CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var processLock = await AcquireProcessLockAsync(cancellationToken).ConfigureAwait(false);
            return (await ReadAsync(cancellationToken).ConfigureAwait(false))
                .Where(record => record.State is ApprovalState.Succeeded or ApprovalState.Indeterminate or
                    ApprovalState.Failed or ApprovalState.RollbackExecuting or ApprovalState.RolledBack or
                    ApprovalState.RollbackIndeterminate)
                .OrderByDescending(record => record.CompletedAt ?? record.CreatedAt)
                .Take(200)
                .ToArray();
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<bool> HasConflictingReconciliationEvidenceAsync(
        string approvalId,
        IReadOnlyList<ReconciledImportFile> evidence,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(approvalId)) throw new ArgumentException("Approval ID is required.", nameof(approvalId));
        ArgumentNullException.ThrowIfNull(evidence);
        var trackFileIds = evidence.Select(item => item.TrackFileId).ToHashSet();
        var importedPaths = evidence.Select(item => item.ImportedPath).ToHashSet(StringComparer.Ordinal);
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var processLock = await AcquireProcessLockAsync(cancellationToken).ConfigureAwait(false);
            return (await ReadAsync(cancellationToken).ConfigureAwait(false))
                .Where(record => !string.Equals(record.ApprovalId, approvalId, StringComparison.Ordinal))
                .SelectMany(record => record.ReconciledFiles ?? Array.Empty<ReconciledImportFile>())
                .Any(item => trackFileIds.Contains(item.TrackFileId) || importedPaths.Contains(item.ImportedPath));
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task BeginActivityCleanupAsync(string downloadId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(downloadId)) throw new ArgumentException("Download ID is required.", nameof(downloadId));
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var processLock = await AcquireProcessLockAsync(cancellationToken).ConfigureAwait(false);
            var records = await ReadAsync(cancellationToken).ConfigureAwait(false);
            var index = records.FindLastIndex(record =>
            {
                if (record.State != ApprovalState.Succeeded || record.ActivityCleanupStartedAt is not null) return false;
                try
                {
                    return string.Equals(record.Payload.Deserialize<ApprovedImportPlan>()?.DownloadId, downloadId, StringComparison.Ordinal);
                }
                catch (JsonException) { return false; }
            });
            if (index < 0)
                throw new InvalidOperationException("No successful AI Import audit record owns this tracked download.");
            records[index] = records[index] with
            {
                ActivityCleanupStartedAt = _timeProvider.GetUtcNow(),
                ActivityCleanupOutcome = "Activity cleanup started; completion has not yet been verified."
            };
            await WriteAsync(records, cancellationToken).ConfigureAwait(false);
        }
        finally { _lock.Release(); }
    }

    public async Task CompleteActivityCleanupAsync(
        string downloadId,
        bool complete,
        string outcome,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(outcome)) throw new ArgumentException("Outcome is required.", nameof(outcome));
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var processLock = await AcquireProcessLockAsync(cancellationToken).ConfigureAwait(false);
            var records = await ReadAsync(cancellationToken).ConfigureAwait(false);
            var index = records.FindLastIndex(record =>
            {
                if (record.ActivityCleanupStartedAt is null || record.ActivityCleanupCompletedAt is not null) return false;
                try
                {
                    return string.Equals(record.Payload.Deserialize<ApprovedImportPlan>()?.DownloadId, downloadId, StringComparison.Ordinal);
                }
                catch (JsonException) { return false; }
            });
            if (index < 0) throw new InvalidOperationException("Activity cleanup audit state was not found.");
            records[index] = records[index] with
            {
                ActivityCleanupCompletedAt = complete ? _timeProvider.GetUtcNow() : null,
                ActivityCleanupOutcome = outcome
            };
            await WriteAsync(records, cancellationToken).ConfigureAwait(false);
        }
        finally { _lock.Release(); }
    }

    public async Task BeginRollbackAsync(string approvalId, CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var processLock = await AcquireProcessLockAsync(cancellationToken).ConfigureAwait(false);
            var records = await ReadAsync(cancellationToken).ConfigureAwait(false);
            var index = records.FindIndex(record => string.Equals(record.ApprovalId, approvalId, StringComparison.Ordinal));
            if (index < 0) throw new KeyNotFoundException("Import review record was not found.");
            var record = records[index];
            if (record.State != ApprovalState.Succeeded || record.ReconciledFiles is not { Count: > 0 })
                throw new ApprovalReplayException("Only a completely reconciled successful import can begin rollback.");
            records[index] = record with { State = ApprovalState.RollbackExecuting };
            await WriteAsync(records, cancellationToken).ConfigureAwait(false);
        }
        finally { _lock.Release(); }
    }

    public async Task CompleteRollbackAsync(
        string approvalId,
        bool complete,
        string outcome,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(outcome)) throw new ArgumentException("Outcome is required.", nameof(outcome));
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var processLock = await AcquireProcessLockAsync(cancellationToken).ConfigureAwait(false);
            var records = await ReadAsync(cancellationToken).ConfigureAwait(false);
            var index = records.FindIndex(record => string.Equals(record.ApprovalId, approvalId, StringComparison.Ordinal));
            if (index < 0) throw new KeyNotFoundException("Import review record was not found.");
            var record = records[index];
            if (record.State != ApprovalState.RollbackExecuting)
                throw new ApprovalReplayException("Only an executing rollback can be completed.");
            records[index] = record with
            {
                State = complete ? ApprovalState.RolledBack : ApprovalState.RollbackIndeterminate,
                RolledBackAt = complete ? _timeProvider.GetUtcNow() : null,
                Outcome = outcome
            };
            await WriteAsync(records, cancellationToken).ConfigureAwait(false);
        }
        finally { _lock.Release(); }
    }

    private async Task<FileStream> AcquireProcessLockAsync(CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(_path)
            ?? throw new InvalidOperationException("Approval store has no parent directory.");
        Directory.CreateDirectory(directory);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var lockPath = _path + ".lock";
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var stream = new FileStream(
                    lockPath,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None,
                    1,
                    FileOptions.Asynchronous | FileOptions.WriteThrough);
                if (!OperatingSystem.IsWindows())
                    File.SetUnixFileMode(lockPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                return stream;
            }
            catch (IOException) when (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(25), cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async Task<List<ApprovalRecord>> ReadAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_path)) return [];
        await using var stream = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous);
        return await JsonSerializer.DeserializeAsync<List<ApprovalRecord>>(stream, SerializerOptions, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("Approval store is empty or malformed.");
    }

    private async Task WriteAsync(List<ApprovalRecord> records, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(_path) ?? throw new InvalidOperationException("Approval store has no parent directory.");
        Directory.CreateDirectory(directory);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var temporary = Path.Combine(directory, "." + Path.GetFileName(_path) + "." + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                await JsonSerializer.SerializeAsync(stream, records, SerializerOptions, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
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

    private static bool TokenMatches(ApprovalRecord record, string token)
    {
        if (string.IsNullOrWhiteSpace(token)) return false;
        return CryptographicOperations.FixedTimeEquals(
            Convert.FromHexString(record.TokenHash),
            Convert.FromHexString(HashToken(token)));
    }

    private static string HashToken(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
    private static string Base64Url(byte[] value) => Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static void FlushDirectory(string directory)
    {
        if (!OperatingSystem.IsLinux()) return;
        const int readOnly = 0;
        const int directoryFlag = 0x10000;
        const int closeOnExec = 0x80000;
        var descriptor = open(directory, readOnly | directoryFlag | closeOnExec);
        if (descriptor < 0) throw new IOException("Unable to open the approval-store directory for durability.");
        try
        {
            if (fsync(descriptor) != 0) throw new IOException("Unable to durably flush the approval-store directory.");
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
