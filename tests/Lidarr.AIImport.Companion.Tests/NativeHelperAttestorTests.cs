using Lidarr.AIImport.Companion.Filesystem;
using System.Diagnostics;

namespace Lidarr.AIImport.Companion.Tests;

[TestFixture]
public sealed class NativeHelperAttestorTests
{
    [Test]
    [System.Runtime.Versioning.SupportedOSPlatform("linux")]
    public async Task Rejects_helper_changed_after_startup()
    {
        using var temp = new TempDirectory();
        var helper = Path.Combine(temp.Path, "helper");
        await File.WriteAllTextAsync(helper, "first");
        File.SetUnixFileMode(helper, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        var attestor = new NativeHelperAttestor(helper);
        File.SetUnixFileMode(helper, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        await File.WriteAllTextAsync(helper, "second");

        Assert.ThrowsAsync<InvalidOperationException>(() => attestor.VerifyAsync(CancellationToken.None));
    }

    [Test]
    [System.Runtime.Versioning.SupportedOSPlatform("linux")]
    public async Task Executes_the_same_descriptor_that_was_attested()
    {
        if (!OperatingSystem.IsLinux()) Assert.Ignore("Descriptor execution is Linux-specific.");
        using var temp = new TempDirectory();
        var helper = Path.Combine(temp.Path, "helper.sh");
        await File.WriteAllTextAsync(helper, "#!/bin/sh\nprintf 'descriptor-bound\\n'\n");
        File.SetUnixFileMode(helper, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        var attestor = new NativeHelperAttestor(helper);

        var result = await attestor.RunAsync(
            new ProcessStartInfo(helper), ReadOnlyMemory<byte>.Empty, 256,
            TimeSpan.FromSeconds(5), CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(result.ExitCode, Is.Zero);
            Assert.That(result.StandardOutput, Is.EqualTo("descriptor-bound\n"));
        });
    }

    private sealed class TempDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "ai-import-attestor-" + Guid.NewGuid().ToString("N"));
        public TempDirectory() => Directory.CreateDirectory(Path);
        public void Dispose() => Directory.Delete(Path, true);
    }
}
