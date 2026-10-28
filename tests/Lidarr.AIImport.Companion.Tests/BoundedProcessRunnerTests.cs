using System.Diagnostics;
using Lidarr.AIImport.Companion.Filesystem;

namespace Lidarr.AIImport.Companion.Tests;

[TestFixture]
public sealed class BoundedProcessRunnerTests
{
    [Test]
    public void Kills_process_when_output_exceeds_bound()
    {
        if (!OperatingSystem.IsLinux()) Assert.Ignore("Linux process semantics required.");
        var start = new ProcessStartInfo("/bin/sh");
        start.ArgumentList.Add("-c");
        start.ArgumentList.Add("while :; do printf 1234567890; done");

        Assert.ThrowsAsync<InvalidDataException>(() => BoundedProcessRunner.RunAsync(
            start, ReadOnlyMemory<byte>.Empty, 128, TimeSpan.FromSeconds(5), CancellationToken.None));
    }

    [Test]
    public void Kills_process_when_cancelled()
    {
        if (!OperatingSystem.IsLinux()) Assert.Ignore("Linux process semantics required.");
        var start = new ProcessStartInfo("/bin/sh");
        start.ArgumentList.Add("-c");
        start.ArgumentList.Add("sleep 30");
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        var exception = Assert.CatchAsync<OperationCanceledException>(() => BoundedProcessRunner.RunAsync(
            start, ReadOnlyMemory<byte>.Empty, 128, TimeSpan.FromSeconds(5), cancellation.Token));
        Assert.That(exception, Is.InstanceOf<OperationCanceledException>());
    }
}
