using Lidarr.AIImport.Companion.Filesystem;

namespace Lidarr.AIImport.Companion.Tests;

[TestFixture]
public sealed class NativeFileIdentityProviderTests
{
    [Test]
    public async Task Invokes_helper_without_shell_and_parses_strict_identity()
    {
        using var temp = new TempDirectory();
        var helper = Path.Combine(temp.Path, "helper");
        await File.WriteAllTextAsync(helper, "#!/bin/sh\n[ \"$1\" = --inspect ] || exit 20\n[ \"$2\" = '/music/a b.flac' ] || exit 21\nprintf '%s\\n' '{\"device\":1,\"inode\":2,\"size\":3,\"mtimeSeconds\":4,\"mtimeNanoseconds\":5,\"ctimeSeconds\":8,\"ctimeNanoseconds\":9,\"mode\":420,\"uid\":6,\"gid\":7,\"links\":1}'\n");
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(helper, UnixFileMode.UserRead | UnixFileMode.UserExecute);

        var identity = await new NativeFileIdentityProvider(helper, TimeSpan.FromSeconds(5))
            .InspectAsync("/music/a b.flac", CancellationToken.None);

        Assert.That(identity, Is.EqualTo(new FileIdentity(1, 2, 3, 4, 5, 8, 9, 420, 6, 7, 1)));
    }

    [Test]
    public void Rejects_unknown_or_missing_identity_fields()
    {
        Assert.Throws<InvalidDataException>(() => NativeFileIdentityProvider.Parse(
            "{\"device\":1,\"inode\":2,\"size\":3,\"mtimeSeconds\":4,\"mtimeNanoseconds\":5,\"mode\":420,\"uid\":6,\"gid\":7,\"links\":1,\"path\":\"x\"}"));
        Assert.Throws<InvalidDataException>(() => NativeFileIdentityProvider.Parse(
            "{\"device\":1,\"inode\":2}"));
    }

    [Test]
    public async Task Nonzero_helper_exit_fails_closed()
    {
        using var temp = new TempDirectory();
        var helper = Path.Combine(temp.Path, "helper");
        await File.WriteAllTextAsync(helper, "#!/bin/sh\nprintf 'rejected\\n' >&2\nexit 22\n");
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(helper, UnixFileMode.UserRead | UnixFileMode.UserExecute);

        Assert.ThrowsAsync<InvalidOperationException>(() =>
            new NativeFileIdentityProvider(helper, TimeSpan.FromSeconds(5))
                .InspectAsync("/music/track.flac", CancellationToken.None));
    }

    private sealed class TempDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ai-import-inspect-" + Guid.NewGuid().ToString("N"));
        public TempDirectory() => Directory.CreateDirectory(Path);
        public void Dispose() => Directory.Delete(Path, true);
    }
}
