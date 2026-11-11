using System.Text.Json;
using NzbDrone.Common.EnvironmentInfo;

namespace Lidarr.Plugin.AIImport.Workflows;

public sealed record YoloRuntimeState(DateTimeOffset? LastPeriodicDiscoveryQueuedAt);

public sealed class YoloRuntimeStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly string _path;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public YoloRuntimeStore(IAppFolderInfo appFolderInfo)
        : this(new StandaloneStatePaths(appFolderInfo).YoloRuntimeStore)
    {
    }

    internal YoloRuntimeStore(string path)
    {
        _path = Path.GetFullPath(path);
    }

    public async Task<YoloRuntimeState> ReadAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var processLock = await AcquireProcessLockAsync(cancellationToken).ConfigureAwait(false);
            if (!File.Exists(_path)) return new YoloRuntimeState(null);
            await using var stream = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            return await JsonSerializer.DeserializeAsync<YoloRuntimeState>(stream, JsonOptions, cancellationToken).ConfigureAwait(false)
                   ?? throw new InvalidDataException("YOLO runtime state is empty.");
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task RecordPeriodicDiscoveryQueuedAsync(DateTimeOffset queuedAt, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var processLock = await AcquireProcessLockAsync(cancellationToken).ConfigureAwait(false);
            var directory = Path.GetDirectoryName(_path)!;
            Directory.CreateDirectory(directory);
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            var temporary = _path + ".tmp";
            await using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 4096,
                             FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, new YoloRuntimeState(queuedAt), JsonOptions, cancellationToken)
                    .ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(true);
            }
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            File.Move(temporary, _path, true);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<FileStream> AcquireProcessLockAsync(CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(_path)!;
        Directory.CreateDirectory(directory);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var lockPath = _path + ".lock";
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var stream = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1,
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
}
