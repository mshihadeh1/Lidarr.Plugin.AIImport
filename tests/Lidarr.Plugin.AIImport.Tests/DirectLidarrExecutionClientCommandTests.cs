using Lidarr.Plugin.AIImport.Lidarr;
using NzbDrone.Core.DecisionEngine;
using NzbDrone.Core.Music;
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

    [Test]
    public void Human_selected_mapping_does_not_require_Lidarrs_automatic_track_guess()
    {
        var reviewed = ReviewedItem(trackId: 2002);

        Assert.Multiple(() =>
        {
            Assert.That(DirectLidarrExecutionClient.MatchesApprovedCorrectiveMapping(
                reviewed, humanOverride: true, artistId: 5, albumId: 10, releaseId: 101, approvedTrackId: 1001), Is.True);
            Assert.That(DirectLidarrExecutionClient.MatchesApprovedCorrectiveMapping(
                reviewed, humanOverride: false, artistId: 5, albumId: 10, releaseId: 101, approvedTrackId: 1001), Is.False);
        });
    }

    [Test]
    public void Human_selected_mapping_still_rejects_Lidarr_rejections_or_changed_ownership()
    {
        var rejected = ReviewedItem(trackId: 2002);
        rejected.Rejections = [new Rejection("Source is not importable")];
        var wrongRelease = ReviewedItem(trackId: 2002);
        wrongRelease.Release = new AlbumRelease { Id = 999, AlbumId = 10 };

        Assert.Multiple(() =>
        {
            Assert.That(DirectLidarrExecutionClient.MatchesApprovedCorrectiveMapping(
                rejected, humanOverride: true, artistId: 5, albumId: 10, releaseId: 101, approvedTrackId: 1001), Is.False);
            Assert.That(DirectLidarrExecutionClient.MatchesApprovedCorrectiveMapping(
                wrongRelease, humanOverride: true, artistId: 5, albumId: 10, releaseId: 101, approvedTrackId: 1001), Is.False);
        });
    }


    [Test]
    public void Existing_library_decisions_are_not_pending_files_for_the_download()
    {
        var pending = InProcessLidarrGateway.FilterPendingDownloadFiles(
        [
            new ManualImportItem { Id = 1, DownloadId = "download" },
            new ManualImportItem { Id = 2, DownloadId = null }
        ], "download");

        Assert.That(pending.Select(x => x.Id), Is.EqualTo(new[] { 1 }));
    }

    [Test]
    public void A_manual_import_row_for_another_download_fails_closed()
    {
        Assert.That(() => InProcessLidarrGateway.FilterPendingDownloadFiles(
            [new ManualImportItem { DownloadId = "other" }], "download"),
            Throws.TypeOf<InvalidDataException>());
    }

    private static ManualImportItem ReviewedItem(int trackId) => new()
    {
        Artist = new Artist { Id = 5 },
        Album = new Album { Id = 10, ArtistId = 5 },
        Release = new AlbumRelease { Id = 101, AlbumId = 10 },
        Tracks = [new Track { Id = trackId }],
        Rejections = []
    };

    private static ManualImportCommand CommandWith(ManualImportFile file) => new()
    {
        ImportMode = ImportMode.Copy,
        ReplaceExistingFiles = false,
        Files = [file]
    };
}
