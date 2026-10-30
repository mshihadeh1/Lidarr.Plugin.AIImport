using Lidarr.Plugin.AIImport.Authorization;

namespace Lidarr.Plugin.AIImport.Tests;

[TestFixture]
public sealed class TransferAuthorizationStoreDirectoryTests
{
    [Test]
    [System.Runtime.Versioning.SupportedOSPlatform("linux")]
    public void Existing_authorization_directory_is_restricted_to_0700_without_deleting_contents()
    {
        if (!OperatingSystem.IsLinux()) Assert.Ignore("Unix directory modes are Linux-specific.");
        using var temp = new TempDirectory();
        var directory = Path.Combine(temp.Path, "authorizations");
        Directory.CreateDirectory(directory);
        var marker = Path.Combine(directory, "keep");
        File.WriteAllText(marker, "preserve");
        File.SetUnixFileMode(directory,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
            UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
            UnixFileMode.OtherRead | UnixFileMode.OtherExecute);

        _ = new TransferAuthorizationStore(directory);

        Assert.Multiple(() =>
        {
            Assert.That(File.GetUnixFileMode(directory), Is.EqualTo(
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute));
            Assert.That(File.ReadAllText(marker), Is.EqualTo("preserve"));
        });
    }

    [Test]
    [System.Runtime.Versioning.SupportedOSPlatform("linux")]
    public void Symbolic_link_authorization_directory_is_rejected_without_changing_its_target()
    {
        if (!OperatingSystem.IsLinux()) Assert.Ignore("Unix directory modes are Linux-specific.");
        using var temp = new TempDirectory();
        var target = Path.Combine(temp.Path, "target");
        var link = Path.Combine(temp.Path, "authorizations");
        Directory.CreateDirectory(target);
        var targetMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                         UnixFileMode.GroupRead | UnixFileMode.GroupExecute;
        File.SetUnixFileMode(target, targetMode);
        Directory.CreateSymbolicLink(link, target);

        Assert.Throws<IOException>(() => _ = new TransferAuthorizationStore(link));
        Assert.That(File.GetUnixFileMode(target), Is.EqualTo(targetMode));
    }

    private sealed class TempDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ai-import-auth-dir-" + Guid.NewGuid().ToString("N"));
        public TempDirectory() => Directory.CreateDirectory(Path);
        public void Dispose() => Directory.Delete(Path, true);
    }
}
