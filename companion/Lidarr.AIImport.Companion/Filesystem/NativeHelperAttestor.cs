using System.Security.Cryptography;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Lidarr.AIImport.Companion.Filesystem;

public sealed class NativeHelperAttestor
{
    private static readonly SemaphoreSlim ExecutionLock = new(1, 1);
    private readonly string _path;
    private readonly byte[] _expectedHash;

    public NativeHelperAttestor(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
            throw new ArgumentException("Native helper path must be absolute.", nameof(path));
        _path = Path.GetFullPath(path);
        _expectedHash = ReadAndValidateAsync(CancellationToken.None).GetAwaiter().GetResult();
    }

    public async Task VerifyAsync(CancellationToken cancellationToken)
    {
        var actual = await ReadAndValidateAsync(cancellationToken).ConfigureAwait(false);
        if (!CryptographicOperations.FixedTimeEquals(actual, _expectedHash))
            throw new InvalidOperationException("Native helper binary changed after startup; execution is forbidden.");
    }

    public string ExpectedSha256 => Convert.ToHexString(_expectedHash).ToLowerInvariant();

    public async Task<ProcessRunResult> RunAsync(
        ProcessStartInfo start,
        ReadOnlyMemory<byte> standardInput,
        int outputLimitBytes,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsLinux())
            throw new PlatformNotSupportedException("Descriptor-bound native helper execution requires Linux.");
        ArgumentNullException.ThrowIfNull(start);
        await ExecutionLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var stream = OpenValidatedStream();
            var actual = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
            if (!CryptographicOperations.FixedTimeEquals(actual, _expectedHash))
                throw new InvalidOperationException("Native helper binary changed after startup; execution is forbidden.");
            var descriptor = stream.SafeFileHandle.DangerousGetHandle().ToInt32();
            var flags = fcntl(descriptor, F_GETFD, 0);
            if (flags < 0 || fcntl(descriptor, F_SETFD, flags & ~FD_CLOEXEC) < 0)
                throw new InvalidOperationException("Native helper descriptor cannot be inherited for exact execution.");
            start.FileName = $"/proc/self/fd/{descriptor}";
            return await BoundedProcessRunner.RunAsync(
                start, standardInput, outputLimitBytes, timeout, cancellationToken,
                () =>
                {
                    if (fcntl(descriptor, F_SETFD, flags) < 0)
                        throw new InvalidOperationException("Native helper descriptor inheritance could not be revoked.");
                }).ConfigureAwait(false);
        }
        finally
        {
            ExecutionLock.Release();
        }
    }

    private async Task<byte[]> ReadAndValidateAsync(CancellationToken cancellationToken)
    {
        await using var stream = OpenValidatedStream();
        return await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
    }

    private FileStream OpenValidatedStream()
    {
        if (!OperatingSystem.IsLinux())
            throw new PlatformNotSupportedException("Native helper attestation requires Linux.");
        var info = new FileInfo(_path);
        if (!info.Exists || info.LinkTarget is not null)
            throw new InvalidOperationException("Native helper must be an existing non-symlink file.");
        if ((info.Attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
            throw new InvalidOperationException("Native helper path is not a regular file.");
        var mode = File.GetUnixFileMode(_path);
        if ((mode & (UnixFileMode.UserWrite | UnixFileMode.GroupWrite | UnixFileMode.OtherWrite)) != 0)
            throw new InvalidOperationException("Native helper must not be writable by any user.");
        var stream = new FileStream(
            _path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (fstat(stream.SafeFileHandle.DangerousGetHandle().ToInt32(), out var status) != 0 ||
            status.UserId != 0 || status.LinkCount != 1 || (status.Mode & FileTypeMask) != RegularFile)
        {
            stream.Dispose();
            throw new InvalidOperationException("Native helper must be a root-owned, single-link regular file.");
        }
        return stream;
    }

    private const int F_GETFD = 1;
    private const int F_SETFD = 2;
    private const int FD_CLOEXEC = 1;
    private const uint FileTypeMask = 0xF000;
    private const uint RegularFile = 0x8000;

    [StructLayout(LayoutKind.Sequential)]
    private struct LinuxStat
    {
        public ulong Device;
        public ulong Inode;
        public ulong LinkCount;
        public uint Mode;
        public uint UserId;
        public uint GroupId;
        private readonly int Padding;
        public ulong RDevice;
        public long Size;
        public long BlockSize;
        public long Blocks;
        public long AccessSeconds;
        public long AccessNanoseconds;
        public long ModificationSeconds;
        public long ModificationNanoseconds;
        public long ChangeSeconds;
        public long ChangeNanoseconds;
        private readonly long Reserved0;
        private readonly long Reserved1;
        private readonly long Reserved2;
    }

    [DllImport("libc", SetLastError = true)]
    private static extern int fcntl(int fd, int command, int argument);

    [DllImport("libc", SetLastError = true)]
    private static extern int fstat(int fd, out LinuxStat status);
}
