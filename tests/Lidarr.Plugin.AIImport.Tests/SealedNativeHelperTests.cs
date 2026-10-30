using System.Runtime.InteropServices;
using Lidarr.Plugin.AIImport.Filesystem;

namespace Lidarr.Plugin.AIImport.Tests;

[TestFixture]
[NonParallelizable]
public sealed class SealedNativeHelperTests
{
    [Test]
    [System.Runtime.Versioning.SupportedOSPlatform("linux")]
    public void Install_fails_without_replacing_an_occupied_execution_descriptor()
    {
        if (!OperatingSystem.IsLinux()) Assert.Ignore("Linux-only descriptor test.");
        var descriptors = new int[2];
        Assert.That(pipe(descriptors), Is.Zero);
        var readDescriptor = descriptors[0];
        var writeDescriptor = descriptors[1];
        try
        {
            Assert.That(dup2(writeDescriptor, SealedNativeHelper.ExecutionDescriptor),
                Is.EqualTo(SealedNativeHelper.ExecutionDescriptor));
            if (writeDescriptor != SealedNativeHelper.ExecutionDescriptor)
            {
                Assert.That(close(writeDescriptor), Is.Zero);
                writeDescriptor = -1;
            }

            var repositoryRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../.."));
            var helper = Path.Combine(repositoryRoot, "artifacts", "lidarr-hardlink-import");
            Assert.That(File.Exists(helper), Is.True, helper);

            var error = Assert.Throws<InvalidOperationException>(() => SealedNativeHelper.Install(helper));
            Assert.That(error!.Message, Does.Contain($"descriptor {SealedNativeHelper.ExecutionDescriptor} is already occupied"));

            var marker = new byte[] { 0x5a };
            Assert.That(write(SealedNativeHelper.ExecutionDescriptor, marker, 1), Is.EqualTo((nint)1));
            var observed = new byte[1];
            Assert.That(read(readDescriptor, observed, 1), Is.EqualTo((nint)1));
            Assert.That(observed, Is.EqualTo(marker));
        }
        finally
        {
            close(readDescriptor);
            if (writeDescriptor >= 0) close(writeDescriptor);
            close(SealedNativeHelper.ExecutionDescriptor);
        }
    }

    [DllImport("libc", SetLastError = true)] private static extern int pipe(int[] descriptors);
    [DllImport("libc", SetLastError = true)] private static extern int dup2(int oldDescriptor, int newDescriptor);
    [DllImport("libc", SetLastError = true)] private static extern nint write(int descriptor, byte[] buffer, nuint count);
    [DllImport("libc", SetLastError = true)] private static extern nint read(int descriptor, byte[] buffer, nuint count);
    [DllImport("libc", SetLastError = true)] private static extern int close(int descriptor);
}
