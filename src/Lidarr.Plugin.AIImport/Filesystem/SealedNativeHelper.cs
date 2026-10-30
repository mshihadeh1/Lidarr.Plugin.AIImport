using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;

namespace Lidarr.Plugin.AIImport.Filesystem;

public static class SealedNativeHelper
{
    public const int ExecutionDescriptor = 198;
    public const string ExecutionPath = "/proc/self/fd/198";
    private static readonly object Gate = new();
    private static bool _installed;

    public static string ExpectedSha256 =>
        typeof(SealedNativeHelper).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .SingleOrDefault(value => value.Key == "NativeHelperSha256")?.Value ?? string.Empty;

    public static void Install(string packagedPath)
    {
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException("The strict hardlink helper requires Linux.");
        lock (Gate)
        {
            if (_installed) return;
            var expected = ExpectedSha256;
            if (expected.Length != 64 || expected.Any(c => !Uri.IsHexDigit(c)))
                throw new InvalidOperationException("The build-embedded native helper SHA-256 is missing or invalid.");
            var info = new FileInfo(packagedPath);
            if (!info.Exists || info.LinkTarget is not null || (info.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("The packaged native helper must be an existing non-symlink file.");
            using var source = new FileStream(packagedPath, FileMode.Open, FileAccess.Read, FileShare.Read,
                81920, FileOptions.SequentialScan);
            var actual = Convert.ToHexString(SHA256.HashData(source)).ToLowerInvariant();
            if (!CryptographicOperations.FixedTimeEquals(Convert.FromHexString(actual), Convert.FromHexString(expected)))
                throw new InvalidOperationException("The packaged native helper does not match its build-embedded SHA-256.");
            source.Position = 0;
            var fd = memfd_create("lidarr-ai-import", MFD_ALLOW_SEALING | MFD_CLOEXEC);
            if (fd < 0) throw Error("memfd_create failed");
            try
            {
                using (var target = new FileStream(new SafeFileHandle((IntPtr)fd, ownsHandle: false), FileAccess.Write))
                {
                    source.CopyTo(target);
                    target.Flush(true);
                }
                if (fchmod(fd, 0x16D) != 0) throw Error("fchmod(0555) on the native helper memfd failed");
                if (fcntl(fd, F_ADD_SEALS, F_SEAL_WRITE | F_SEAL_SHRINK | F_SEAL_GROW | F_SEAL_SEAL) < 0)
                    throw Error("sealing the native helper memfd failed");
                if (fd != ExecutionDescriptor)
                {
                    var installedDescriptor = fcntl(fd, F_DUPFD_CLOEXEC, ExecutionDescriptor);
                    if (installedDescriptor < 0) throw Error("duplicating the native helper descriptor failed");
                    if (installedDescriptor != ExecutionDescriptor)
                    {
                        close(installedDescriptor);
                        throw new InvalidOperationException("The native helper cannot start because descriptor 198 is already occupied.");
                    }
                }
                if (fcntl(ExecutionDescriptor, F_SETFD, 0) < 0) throw Error("clearing CLOEXEC on the native helper failed");
                _installed = true;
            }
            finally
            {
                if (fd != ExecutionDescriptor) close(fd);
            }
        }
    }

    private static IOException Error(string message) => new(message, new System.ComponentModel.Win32Exception(Marshal.GetLastPInvokeError()));
    private const uint MFD_CLOEXEC = 0x0001;
    private const uint MFD_ALLOW_SEALING = 0x0002;
    private const int F_SETFD = 2;
    private const int F_DUPFD_CLOEXEC = 1030;
    private const int F_ADD_SEALS = 1033;
    private const int F_SEAL_SEAL = 0x0001;
    private const int F_SEAL_SHRINK = 0x0002;
    private const int F_SEAL_GROW = 0x0004;
    private const int F_SEAL_WRITE = 0x0008;
    [DllImport("libc", SetLastError = true)] private static extern int memfd_create(string name, uint flags);
    [DllImport("libc", SetLastError = true)] private static extern int fchmod(int fd, uint mode);
    [DllImport("libc", SetLastError = true)] private static extern int fcntl(int fd, int command, int argument);
    [DllImport("libc", SetLastError = true)] private static extern int close(int fd);
}