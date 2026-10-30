using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Lidarr.Plugin.AIImport.Approval;
using Lidarr.Plugin.AIImport.Filesystem;
using Lidarr.Plugin.AIImport.Workflows;
using NzbDrone.Common.EnvironmentInfo;

namespace Lidarr.Plugin.AIImport.Authorization;

public sealed record PublishedTransferAuthorization(
    string AuthorizationId,
    string Path,
    string SourceId,
    int TrackId,
    string SourcePath,
    ulong SourceDevice,
    ulong SourceInode);

public interface ITransferAuthorizationStore
{
    Task<IReadOnlyList<PublishedTransferAuthorization>> PublishAsync(
        ApprovedImportPlan plan,
        DateTimeOffset expiresAt,
        CancellationToken cancellationToken);
}

public sealed record ApprovedTransferAuthorization(
    string AuthorizationId,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt,
    ulong SourceDevice,
    ulong SourceInode,
    long SourceSize,
    long MtimeSeconds,
    long MtimeNanoseconds,
    long CtimeSeconds,
    long CtimeNanoseconds,
    uint SourceMode,
    uint SourceUid,
    uint SourceGid,
    int ArtistId,
    int AlbumId,
    int TrackId,
    string SourcePath,
    string DestinationRoot);

public sealed class TransferAuthorizationStore : ITransferAuthorizationStore
{
    private static readonly byte[] Magic = "AIAUTH02"u8.ToArray();
    private const uint Version = 2;
    private const int MaximumRecordBytes = 64 * 1024;
    private const int MaximumPathBytes = 32 * 1024;
    private readonly string _directory;
    private readonly string? _nativeHelperPath;
    private readonly TimeProvider _timeProvider;
    private NativeHelperAttestor? _attestor;

    public TransferAuthorizationStore(IAppFolderInfo appFolderInfo)
        : this(new StandaloneStatePaths(appFolderInfo).AuthorizationDirectory, SealedNativeHelper.ExecutionPath)
    {
        new StandaloneStatePaths(appFolderInfo).ConfigureAuthorizationEnvironment();
    }

    public TransferAuthorizationStore(
        string directory,
        string? nativeHelperPath = null,
        TimeProvider? timeProvider = null,
        NativeHelperAttestor? attestor = null)
    {
        if (!OperatingSystem.IsLinux())
            throw new PlatformNotSupportedException("Transfer authorization requires Linux.");
        if (string.IsNullOrWhiteSpace(directory) || !Path.IsPathFullyQualified(directory))
            throw new ArgumentException("The transfer authorization directory must be absolute.", nameof(directory));
        if (nativeHelperPath is not null && !Path.IsPathFullyQualified(nativeHelperPath))
            throw new ArgumentException("The native helper path must be absolute.", nameof(nativeHelperPath));

        _directory = Path.GetFullPath(directory);
        _nativeHelperPath = nativeHelperPath is null ? null : Path.GetFullPath(nativeHelperPath);
        _timeProvider = timeProvider ?? TimeProvider.System;
        _attestor = attestor;
        var existed = Directory.Exists(_directory);
        if (!existed)
            Directory.CreateDirectory(_directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        if (new DirectoryInfo(_directory).LinkTarget is not null)
            throw new IOException("The transfer authorization directory cannot be a symbolic link.");
        var requiredMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
        if (File.GetUnixFileMode(_directory) != requiredMode)
            throw new IOException("The transfer authorization directory must have mode 0700.");
    }

    public async Task<IReadOnlyList<PublishedTransferAuthorization>> PublishAsync(
        ApprovedImportPlan plan,
        DateTimeOffset expiresAt,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (plan.ArtistId <= 0 || plan.AlbumId <= 0 || plan.Files.Count == 0)
            throw new InvalidDataException("The approved import plan is incomplete.");
        if (string.IsNullOrWhiteSpace(plan.DestinationRoot) || !Path.IsPathFullyQualified(plan.DestinationRoot) ||
            Path.TrimEndingDirectorySeparator(plan.DestinationRoot) != plan.DestinationRoot)
            throw new InvalidDataException("The approved destination root must be an absolute path without a trailing separator.");
        var createdAt = _timeProvider.GetUtcNow();
        if (expiresAt <= createdAt || expiresAt > createdAt.AddHours(24))
            throw new InvalidDataException("Transfer authorization lifetime must be greater than zero and no more than 24 hours.");

        var duplicateIdentity = plan.Files
            .GroupBy(x => (x.Identity.Device, x.Identity.Inode))
            .Any(group => group.Count() != 1);
        if (duplicateIdentity)
            throw new InvalidDataException("An approved plan contains a duplicate source identity.");

        foreach (var file in plan.Files)
        {
            if (file.TrackId <= 0 || file.Identity.Size < 0 || file.Identity.Size != file.Size ||
                string.IsNullOrWhiteSpace(file.SourcePath) || !Path.IsPathFullyQualified(file.SourcePath))
                throw new InvalidDataException("An approved source record is invalid.");
        }
        if (_nativeHelperPath is not null)
            return await PublishWithNativeHelperAsync(plan, createdAt, expiresAt, cancellationToken).ConfigureAwait(false);

        var lockPath = Path.Combine(_directory, ".publish.lock");
        await using var gate = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1,
            FileOptions.Asynchronous | FileOptions.WriteThrough);
        if (OperatingSystem.IsLinux())
            File.SetUnixFileMode(lockPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);

        var existing = Directory.EnumerateFiles(_directory, "*.approved", SearchOption.TopDirectoryOnly)
            .Select(ReadApproved)
            .ToArray();
        foreach (var file in plan.Files)
        {
            if (existing.Any(x => x.SourceDevice == (ulong)file.Identity.Device &&
                                  x.SourceInode == (ulong)file.Identity.Inode &&
                                  x.ExpiresAt >= createdAt))
                throw new IOException("An active transfer authorization already exists for this source inode.");
        }

        var published = new List<PublishedTransferAuthorization>(plan.Files.Count);
        try
        {
            foreach (var file in plan.Files.OrderBy(x => x.SourceId, StringComparer.Ordinal))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var idBytes = RandomNumberGenerator.GetBytes(32);
                var id = Convert.ToHexString(idBytes).ToLowerInvariant();
                var record = new ApprovedTransferAuthorization(
                    id, createdAt, expiresAt, (ulong)file.Identity.Device, (ulong)file.Identity.Inode,
                    file.Identity.Size, file.Identity.MtimeSeconds, file.Identity.MtimeNanoseconds,
                    file.Identity.CtimeSeconds, file.Identity.CtimeNanoseconds, file.Identity.Mode,
                    (uint)file.Identity.Uid, (uint)file.Identity.Gid, plan.ArtistId, plan.AlbumId,
                    file.TrackId, file.SourcePath, plan.DestinationRoot);
                var bytes = Serialize(record, idBytes);
                var finalPath = Path.Combine(_directory, id + ".approved");
                var temporaryPath = Path.Combine(_directory, "." + id + ".tmp");
                await WriteAtomicAsync(temporaryPath, finalPath, bytes, cancellationToken).ConfigureAwait(false);
                published.Add(new PublishedTransferAuthorization(
                    id, finalPath, file.SourceId, file.TrackId, file.SourcePath,
                    file.Identity.Device, file.Identity.Inode));
            }
            SyncDirectory(_directory);
            return published;
        }
        catch
        {
            // Publication is intentionally immutable. Any successfully published subset expires safely;
            // it is never deleted here because pathname rollback would create a race with the helper.
            throw;
        }
    }

    private async Task<IReadOnlyList<PublishedTransferAuthorization>> PublishWithNativeHelperAsync(
        ApprovedImportPlan plan,
        DateTimeOffset createdAt,
        DateTimeOffset expiresAt,
        CancellationToken cancellationToken)
    {
        var prepared = plan.Files.OrderBy(x => x.SourceId, StringComparer.Ordinal).Select(file =>
        {
            var idBytes = RandomNumberGenerator.GetBytes(32);
            var id = Convert.ToHexString(idBytes).ToLowerInvariant();
            var record = new ApprovedTransferAuthorization(
                id, createdAt, expiresAt, file.Identity.Device, file.Identity.Inode, file.Identity.Size,
                file.Identity.MtimeSeconds, file.Identity.MtimeNanoseconds, file.Identity.CtimeSeconds,
                file.Identity.CtimeNanoseconds, file.Identity.Mode, file.Identity.Uid, file.Identity.Gid,
                plan.ArtistId, plan.AlbumId, file.TrackId, file.SourcePath, plan.DestinationRoot);
            return (Id: id, Bytes: Serialize(record, idBytes), File: file);
        }).ToArray();

        var published = new List<PublishedTransferAuthorization>(prepared.Length);
        foreach (var item in prepared)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var fileName = item.Id + ".approved";
            var start = new ProcessStartInfo(_nativeHelperPath!)
            {
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            start.ArgumentList.Add("--publish-approved");
            start.ArgumentList.Add(_directory);
            start.ArgumentList.Add(fileName);
            var result = await (_attestor ??= new NativeHelperAttestor(_nativeHelperPath!)).RunAsync(
                start, item.Bytes, 4096, TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false);
            if (result.ExitCode != 0)
                throw new IOException(
                    $"Native authorization publication failed with exit code {result.ExitCode}: {result.StandardError.Trim()}");
            var finalPath = Path.Combine(_directory, fileName);
            var verified = ReadApproved(finalPath);
            if (!string.Equals(verified.AuthorizationId, item.Id, StringComparison.Ordinal))
                throw new InvalidDataException("Published transfer authorization failed read-back verification.");
            published.Add(new PublishedTransferAuthorization(
                item.Id, finalPath, item.File.SourceId, item.File.TrackId, item.File.SourcePath,
                item.File.Identity.Device, item.File.Identity.Inode));
        }
        return published;
    }

    public static ApprovedTransferAuthorization ReadApproved(string path)
    {
        var bytes = File.ReadAllBytes(path);
        if (bytes.Length > MaximumRecordBytes)
            throw new InvalidDataException("Transfer authorization is too large.");
        var reader = new RecordReader(bytes);
        reader.Require(Magic);
        if (reader.UInt32() != Version)
            throw new InvalidDataException("Unsupported transfer authorization version.");
        var idBytes = reader.Bytes(32);
        var id = Convert.ToHexString(idBytes).ToLowerInvariant();
        if (!string.Equals(Path.GetFileName(path), id + ".approved", StringComparison.Ordinal))
            throw new InvalidDataException("Transfer authorization ID does not match its filename.");
        var created = DateTimeOffset.FromUnixTimeSeconds(reader.Int64());
        var expires = DateTimeOffset.FromUnixTimeSeconds(reader.Int64());
        var sourceDevice = reader.UInt64();
        var sourceInode = reader.UInt64();
        var sourceSize = reader.Int64();
        var mtimeSeconds = reader.Int64();
        var mtimeNanoseconds = reader.Int64();
        var ctimeSeconds = reader.Int64();
        var ctimeNanoseconds = reader.Int64();
        var sourceMode = reader.UInt32();
        var sourceUid = reader.UInt32();
        var sourceGid = reader.UInt32();
        var artistId = reader.Int32();
        var albumId = reader.Int32();
        var trackId = reader.Int32();
        var sourceLength = reader.UInt32();
        var destinationRootLength = reader.UInt32();
        var result = new ApprovedTransferAuthorization(
            id, created, expires, sourceDevice, sourceInode, sourceSize, mtimeSeconds, mtimeNanoseconds,
            ctimeSeconds, ctimeNanoseconds, sourceMode, sourceUid, sourceGid, artistId, albumId, trackId,
            reader.String(sourceLength, MaximumPathBytes), reader.String(destinationRootLength, MaximumPathBytes));
        reader.RequireEnd();
        if (result.ArtistId <= 0 || result.AlbumId <= 0 || result.TrackId <= 0 ||
            result.SourceSize < 0 || result.ExpiresAt <= result.CreatedAt ||
            string.IsNullOrWhiteSpace(result.SourcePath) || !Path.IsPathFullyQualified(result.SourcePath) ||
            string.IsNullOrWhiteSpace(result.DestinationRoot) || !Path.IsPathFullyQualified(result.DestinationRoot) ||
            Path.TrimEndingDirectorySeparator(result.DestinationRoot) != result.DestinationRoot)
            throw new InvalidDataException("Transfer authorization contains invalid values.");
        return result;
    }

    private static byte[] Serialize(ApprovedTransferAuthorization record, byte[] idBytes)
    {
        var pathBytes = Encoding.UTF8.GetBytes(record.SourcePath);
        var rootBytes = Encoding.UTF8.GetBytes(record.DestinationRoot);
        if (pathBytes.Length == 0 || pathBytes.Length > MaximumPathBytes || record.SourcePath.Contains('\0') ||
            rootBytes.Length == 0 || rootBytes.Length > MaximumPathBytes || record.DestinationRoot.Contains('\0'))
            throw new InvalidDataException("A path is not representable in a transfer authorization.");
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.UTF8, true);
        writer.Write(Magic);
        writer.Write(Version);
        writer.Write(idBytes);
        writer.Write(record.CreatedAt.ToUnixTimeSeconds());
        writer.Write(record.ExpiresAt.ToUnixTimeSeconds());
        writer.Write(record.SourceDevice);
        writer.Write(record.SourceInode);
        writer.Write(record.SourceSize);
        writer.Write(record.MtimeSeconds);
        writer.Write(record.MtimeNanoseconds);
        writer.Write(record.CtimeSeconds);
        writer.Write(record.CtimeNanoseconds);
        writer.Write(record.SourceMode);
        writer.Write(record.SourceUid);
        writer.Write(record.SourceGid);
        writer.Write(record.ArtistId);
        writer.Write(record.AlbumId);
        writer.Write(record.TrackId);
        writer.Write((uint)pathBytes.Length);
        writer.Write((uint)rootBytes.Length);
        writer.Write(pathBytes);
        writer.Write(rootBytes);
        writer.Flush();
        if (stream.Length > MaximumRecordBytes)
            throw new InvalidDataException("Transfer authorization is too large.");
        return stream.ToArray();
    }

    private static async Task WriteAtomicAsync(string temporaryPath, string finalPath, byte[] bytes, CancellationToken cancellationToken)
    {
        await using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096,
            FileOptions.Asynchronous | FileOptions.WriteThrough))
        {
            if (OperatingSystem.IsLinux())
                File.SetUnixFileMode(temporaryPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            stream.Flush(true);
        }
        File.Move(temporaryPath, finalPath, false);
    }

    private static void SyncDirectory(string path)
    {
        var descriptor = open(path, O_RDONLY | O_DIRECTORY | O_CLOEXEC);
        if (descriptor < 0)
            throw new IOException("Cannot open the transfer authorization directory for synchronization.", new System.ComponentModel.Win32Exception(Marshal.GetLastPInvokeError()));
        try
        {
            if (fsync(descriptor) != 0)
                throw new IOException("Cannot synchronize the transfer authorization directory.", new System.ComponentModel.Win32Exception(Marshal.GetLastPInvokeError()));
        }
        finally
        {
            close(descriptor);
        }
    }

    private sealed class RecordReader(byte[] bytes)
    {
        private int _offset;
        public void Require(ReadOnlySpan<byte> expected)
        {
            if (_offset + expected.Length > bytes.Length || !bytes.AsSpan(_offset, expected.Length).SequenceEqual(expected))
                throw new InvalidDataException("Transfer authorization magic is invalid.");
            _offset += expected.Length;
        }
        public byte[] Bytes(int count)
        {
            if (count < 0 || _offset + count > bytes.Length) throw new InvalidDataException("Transfer authorization is truncated.");
            var result = bytes.AsSpan(_offset, count).ToArray(); _offset += count; return result;
        }
        public uint UInt32() { var value = BinaryPrimitives.ReadUInt32LittleEndian(Bytes(4)); return value; }
        public int Int32() => unchecked((int)UInt32());
        public ulong UInt64() { var value = BinaryPrimitives.ReadUInt64LittleEndian(Bytes(8)); return value; }
        public long Int64() => unchecked((long)UInt64());
        public string String(uint encodedLength, int maximum)
        {
            var length = checked((int)encodedLength);
            if (length <= 0 || length > maximum) throw new InvalidDataException("Transfer authorization string length is invalid.");
            try { return new UTF8Encoding(false, true).GetString(Bytes(length)); }
            catch (DecoderFallbackException exception) { throw new InvalidDataException("Transfer authorization contains invalid UTF-8.", exception); }
        }
        public void RequireEnd() { if (_offset != bytes.Length) throw new InvalidDataException("Transfer authorization has trailing bytes."); }
    }

    private const int O_RDONLY = 0;
    private const int O_DIRECTORY = 0x10000;
    private const int O_CLOEXEC = 0x80000;
    [DllImport("libc", SetLastError = true)] private static extern int open(string pathname, int flags);
    [DllImport("libc", SetLastError = true)] private static extern int fsync(int fd);
    [DllImport("libc", SetLastError = true)] private static extern int close(int fd);
}
