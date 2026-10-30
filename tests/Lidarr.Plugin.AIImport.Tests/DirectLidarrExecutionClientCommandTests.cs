using Lidarr.Plugin.AIImport.Lidarr;
using NzbDrone.Core.MediaFiles.TrackImport;
using NzbDrone.Core.MediaFiles.TrackImport.Manual;
using NzbDrone.Core.Messaging.Commands;

namespace Lidarr.Plugin.AIImport.Tests;

[TestFixture]
public sealed class DirectLidarrExecutionClientCommandTests
{
    [Test]
    public void Fresh_manual_import_file_prevents_queue_deduplication_against_stale_command()
    {
        var stale = CommandWith(new ManualImportFile { Path = "/downloads/album/track.flac" });
        var fresh = CommandWith(new FreshManualImportFile { Path = "/downloads/album/track.flac" });

        Assert.That(CommandEqualityComparer.Instance.Equals(stale, fresh), Is.False);
        Assert.That(CommandEqualityComparer.Instance.Equals(fresh, stale), Is.False);
    }

    [Test]
    public void Overlap_detection_rejects_an_active_manual_import_for_any_approved_source()
    {
        var active = new CommandModel
        {
            Name = "ManualImport",
            Status = CommandStatus.Started,
            Body = CommandWith(new ManualImportFile { Path = "/downloads/album/track.flac" })
        };

        Assert.That(DirectLidarrExecutionClient.HasOverlappingActiveManualImport(
            [active], ["/downloads/album/track.flac", "/downloads/album/other.flac"]), Is.True);
        Assert.That(DirectLidarrExecutionClient.HasOverlappingActiveManualImport(
            [active], ["/downloads/different.flac"]), Is.False);
    }

    private static ManualImportCommand CommandWith(ManualImportFile file) => new()
    {
        ImportMode = ImportMode.Copy,
        ReplaceExistingFiles = false,
        Files = [file]
    };
}
