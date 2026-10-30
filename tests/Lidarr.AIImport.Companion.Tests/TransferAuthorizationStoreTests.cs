using Lidarr.AIImport.Companion.Approval;
using Lidarr.AIImport.Companion.Authorization;
using Lidarr.AIImport.Companion.Filesystem;
using System.Text.Json.Nodes;
using System.Runtime.Versioning;

namespace Lidarr.AIImport.Companion.Tests;

[TestFixture]
[SupportedOSPlatform("linux")]
public sealed class TransferAuthorizationStoreTests
{
    [Test]
    public async Task Publishes_bounded_single_use_authorization_per_source()
    {
        using var temp = new TempDirectory();
        var store = new TransferAuthorizationStore(temp.Path);
        var plan = new ApprovedImportPlan(
            "analysis", "download", 5, 10, 101, "safety",
            [new ApprovedImportFile(
                "source", 77, "/downloads/track.flac", 1001,
                JsonNode.Parse("{\"quality\":{\"id\":1}}")!.AsObject(), 0, "download", 123,
                new FileIdentity(1, 2, 123, 4, 5, 8, 9, 420, 6, 7, 1))], "/music/Artist");
        var expires = DateTimeOffset.UtcNow.AddMinutes(10);

        var published = await store.PublishAsync(plan, expires, CancellationToken.None);
        var record = TransferAuthorizationStore.ReadApproved(published.Single().Path);

        Assert.Multiple(() =>
        {
            Assert.That(record.SourcePath, Is.EqualTo("/downloads/track.flac"));
            Assert.That(record.DestinationRoot, Is.EqualTo("/music/Artist"));
            Assert.That(record.SourceDevice, Is.EqualTo(1));
            Assert.That(record.SourceInode, Is.EqualTo(2));
            Assert.That(record.ArtistId, Is.EqualTo(5));
            Assert.That(record.AlbumId, Is.EqualTo(10));
            Assert.That(record.TrackId, Is.EqualTo(1001));
            Assert.That(record.ExpiresAt.ToUnixTimeSeconds(), Is.EqualTo(expires.ToUnixTimeSeconds()));
            Assert.That(File.GetUnixFileMode(temp.Path), Is.EqualTo(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute));
            Assert.That(File.GetUnixFileMode(published.Single().Path), Is.EqualTo(UnixFileMode.UserRead | UnixFileMode.UserWrite));
        });
    }

    [Test]
    public async Task Refuses_a_second_active_authorization_for_same_inode()
    {
        using var temp = new TempDirectory();
        var store = new TransferAuthorizationStore(temp.Path);
        var file = new ApprovedImportFile(
            "source", 77, "/downloads/track.flac", 1001,
            new JsonObject { ["quality"] = new JsonObject { ["id"] = 1 } }, 0, "download", 123,
            new FileIdentity(1, 2, 123, 4, 5, 8, 9, 420, 6, 7, 1));
        var plan = new ApprovedImportPlan("analysis", "download", 5, 10, 101, "safety", [file], "/music/Artist");
        await store.PublishAsync(plan, DateTimeOffset.UtcNow.AddMinutes(10), CancellationToken.None);

        Assert.ThrowsAsync<IOException>(() =>
            store.PublishAsync(plan, DateTimeOffset.UtcNow.AddMinutes(10), CancellationToken.None));
    }

    [Test]
    public async Task Allows_replacement_after_existing_authorization_expires()
    {
        using var temp = new TempDirectory();
        var clock = new FixedTimeProvider(DateTimeOffset.Parse("2026-10-27T08:00:00Z"));
        var store = new TransferAuthorizationStore(temp.Path, null, clock);
        var file = ApprovedFile("source", "/downloads/track.flac", 1001,
            new FileIdentity(1, 2, 123, 4, 5, 8, 9, 420, 6, 7, 1));
        var plan = new ApprovedImportPlan("analysis", "download", 5, 10, 101, "safety", [file], "/music/Artist");
        await store.PublishAsync(plan, clock.UtcNow.AddMinutes(1), CancellationToken.None);
        clock.UtcNow = clock.UtcNow.AddMinutes(2);

        await store.PublishAsync(plan, clock.UtcNow.AddMinutes(10), CancellationToken.None);

        Assert.That(Directory.EnumerateFiles(temp.Path, "*.approved"), Has.Exactly(2).Items);
    }

    [Test]
    public void Rejects_duplicate_source_identities_within_one_batch()
    {
        using var temp = new TempDirectory();
        var store = new TransferAuthorizationStore(temp.Path);
        var identity = new FileIdentity(1, 2, 123, 4, 5, 8, 9, 420, 6, 7, 1);
        var first = ApprovedFile("first", "/downloads/first.flac", 1001, identity);
        var second = ApprovedFile("second", "/downloads/second.flac", 1002, identity);
        var plan = new ApprovedImportPlan("analysis", "download", 5, 10, 101, "safety", [first, second], "/music/Artist");

        Assert.ThrowsAsync<InvalidDataException>(() =>
            store.PublishAsync(plan, DateTimeOffset.UtcNow.AddMinutes(10), CancellationToken.None));
        Assert.That(Directory.EnumerateFiles(temp.Path, "*.approved"), Is.Empty);
    }

    [Test]
    public void Rejects_authorizations_lasting_more_than_24_hours()
    {
        using var temp = new TempDirectory();
        var store = new TransferAuthorizationStore(temp.Path);
        var plan = new ApprovedImportPlan("analysis", "download", 5, 10, 101, "safety",
            [ApprovedFile("source", "/downloads/track.flac", 1001, new FileIdentity(1, 2, 123, 4, 5, 8, 9, 420, 6, 7, 1))], "/music/Artist");

        Assert.ThrowsAsync<InvalidDataException>(() =>
            store.PublishAsync(plan, DateTimeOffset.UtcNow.AddHours(25), CancellationToken.None));
    }

    [Test]
    public void Rejects_an_existing_authorization_directory_with_permissive_mode()
    {
        using var temp = new TempDirectory();
        File.SetUnixFileMode(temp.Path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
            UnixFileMode.GroupRead | UnixFileMode.GroupExecute);

        Assert.Throws<IOException>(() => new TransferAuthorizationStore(temp.Path));
    }

    [Test]
    public async Task Uses_the_native_publisher_and_reads_back_the_exact_record()
    {
        using var temp = new TempDirectory();
        var helper = System.IO.Path.Combine(temp.Path, "publisher.sh");
        await File.WriteAllTextAsync(helper, "#!/bin/sh\n[ \"$1\" = --publish-approved ] || exit 90\ncat > \"$2/$3\"\nchmod 0600 \"$2/$3\"\n");
        File.SetUnixFileMode(helper, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        var store = new TransferAuthorizationStore(temp.Path, helper);
        var plan = new ApprovedImportPlan("analysis", "download", 5, 10, 101, "safety",
            [ApprovedFile("source", "/downloads/native.flac", 1001, new FileIdentity(1, 2, 123, 4, 5, 8, 9, 420, 6, 7, 1))], "/music/Artist");

        var published = await store.PublishAsync(plan, DateTimeOffset.UtcNow.AddMinutes(10), CancellationToken.None);
        var record = TransferAuthorizationStore.ReadApproved(published.Single().Path);

        Assert.That(record.SourcePath, Is.EqualTo("/downloads/native.flac"));
    }

    private static ApprovedImportFile ApprovedFile(string id, string path, int trackId, FileIdentity identity) =>
        new(id, 77, path, trackId, new JsonObject { ["quality"] = new JsonObject { ["id"] = 1 } },
            0, "download", identity.Size, identity);

    private sealed class FixedTimeProvider(DateTimeOffset value) : TimeProvider
    {
        public DateTimeOffset UtcNow { get; set; } = value;
        public override DateTimeOffset GetUtcNow() => UtcNow;
    }

    private sealed class TempDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ai-import-auth-" + Guid.NewGuid().ToString("N"));
        public TempDirectory() => Directory.CreateDirectory(Path,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        public void Dispose() => Directory.Delete(Path, true);
    }
}
