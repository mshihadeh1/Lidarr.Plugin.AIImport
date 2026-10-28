using System.Diagnostics;
using System.Text;

namespace Lidarr.AIImport.Companion.Filesystem;

public sealed record ProcessRunResult(int ExitCode, string StandardOutput, string StandardError);

public static class BoundedProcessRunner
{
    public static async Task<ProcessRunResult> RunAsync(
        ProcessStartInfo start,
        ReadOnlyMemory<byte> standardInput,
        int outputLimitBytes,
        TimeSpan timeout,
        CancellationToken cancellationToken,
        Action? afterStart = null)
    {
        ArgumentNullException.ThrowIfNull(start);
        if (outputLimitBytes <= 0) throw new ArgumentOutOfRangeException(nameof(outputLimitBytes));
        if (timeout <= TimeSpan.Zero || timeout > TimeSpan.FromMinutes(1))
            throw new ArgumentOutOfRangeException(nameof(timeout));
        start.UseShellExecute = false;
        start.RedirectStandardOutput = true;
        start.RedirectStandardError = true;
        start.RedirectStandardInput = !standardInput.IsEmpty;
        start.CreateNoWindow = true;

        using var process = Process.Start(start)
            ?? throw new InvalidOperationException("Native helper process did not start.");
        try
        {
            afterStart?.Invoke();
        }
        catch
        {
            if (!process.HasExited) process.Kill(true);
            process.WaitForExit();
            throw;
        }
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        var stdout = ReadBoundedAsync(process.StandardOutput.BaseStream, outputLimitBytes, deadline.Token);
        var stderr = ReadBoundedAsync(process.StandardError.BaseStream, outputLimitBytes, deadline.Token);
        var exit = process.WaitForExitAsync(deadline.Token);
        var input = WriteInputAsync(process, standardInput, deadline.Token);

        try
        {
            var pending = new List<Task> { exit, stdout, stderr, input };
            while (pending.Count != 0)
            {
                var completed = await Task.WhenAny(pending).ConfigureAwait(false);
                pending.Remove(completed);
                await completed.ConfigureAwait(false);
            }
            return new ProcessRunResult(process.ExitCode, stdout.Result, stderr.Result);
        }
        catch
        {
            try
            {
                if (!process.HasExited) process.Kill(true);
                await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (InvalidOperationException)
            {
            }
            throw;
        }
    }

    private static async Task WriteInputAsync(
        Process process,
        ReadOnlyMemory<byte> input,
        CancellationToken cancellationToken)
    {
        if (input.IsEmpty) return;
        await process.StandardInput.BaseStream.WriteAsync(input, cancellationToken).ConfigureAwait(false);
        await process.StandardInput.BaseStream.FlushAsync(cancellationToken).ConfigureAwait(false);
        process.StandardInput.Close();
    }

    private static async Task<string> ReadBoundedAsync(
        Stream stream,
        int limit,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[Math.Min(1024, limit + 1)];
        using var output = new MemoryStream(Math.Min(limit, 4096));
        while (true)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            if (output.Length + read > limit)
                throw new InvalidDataException("Native helper output exceeded its bound.");
            output.Write(buffer, 0, read);
        }
        return new UTF8Encoding(false, true).GetString(output.ToArray());
    }
}
