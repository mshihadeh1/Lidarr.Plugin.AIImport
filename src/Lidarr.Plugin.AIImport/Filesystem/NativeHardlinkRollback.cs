using System.Diagnostics;
using System.Globalization;

namespace Lidarr.Plugin.AIImport.Filesystem;

public interface INativeHardlinkRollback
{
    Task UnlinkAsync(string sourcePath, string destinationPath, ulong device, ulong inode, long size, CancellationToken cancellationToken);
}

public sealed class NativeHardlinkRollback : INativeHardlinkRollback
{
    private readonly string _helperPath;
    private readonly TimeSpan _timeout;
    private NativeHelperAttestor? _attestor;

    public NativeHardlinkRollback()
        : this(SealedNativeHelper.ExecutionPath, TimeSpan.FromSeconds(10))
    {
    }

    public NativeHardlinkRollback(string helperPath, TimeSpan timeout, NativeHelperAttestor? attestor = null)
    {
        if (!Path.IsPathFullyQualified(helperPath)) throw new ArgumentException("Native helper path must be absolute.", nameof(helperPath));
        if (timeout <= TimeSpan.Zero || timeout > TimeSpan.FromMinutes(1)) throw new ArgumentOutOfRangeException(nameof(timeout));
        _helperPath = helperPath;
        _timeout = timeout;
        _attestor = attestor;
    }

    public async Task UnlinkAsync(string sourcePath, string destinationPath, ulong device, ulong inode, long size, CancellationToken cancellationToken)
    {
        if (!Path.IsPathFullyQualified(sourcePath) || !Path.IsPathFullyQualified(destinationPath) || inode == 0 || size < 0)
            throw new ArgumentException("Exact unlink requires absolute paths and a complete positive identity.");
        var start = new ProcessStartInfo
        {
            FileName = _helperPath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        start.ArgumentList.Add("--unlink-exact");
        start.ArgumentList.Add(sourcePath);
        start.ArgumentList.Add(destinationPath);
        start.ArgumentList.Add(device.ToString(CultureInfo.InvariantCulture));
        start.ArgumentList.Add(inode.ToString(CultureInfo.InvariantCulture));
        start.ArgumentList.Add(size.ToString(CultureInfo.InvariantCulture));
        var result = await (_attestor ??= new NativeHelperAttestor(_helperPath)).RunAsync(
            start, ReadOnlyMemory<byte>.Empty, 1024, _timeout, cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0)
            throw new InvalidOperationException($"Native exact unlink failed with exit code {result.ExitCode}: {result.StandardError.Trim()}");
    }
}
