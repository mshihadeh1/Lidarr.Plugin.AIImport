using Lidarr.Plugin.AIImport.Lidarr;

namespace Lidarr.Plugin.AIImport.Tests;

[TestFixture]
public sealed class InProcessLidarrGatewayPathTests
{
    [Test]
    public void Untracked_scan_accepts_descendants_but_rejects_sibling_library_files()
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "ai-import-path-test"));
        var album = Path.Combine(root, "downloads", "album-one");

        Assert.Multiple(() =>
        {
            Assert.That(InProcessLidarrGateway.IsSourceInsideScanFolder(album, Path.Combine(album, "disc-1", "01.flac")), Is.True);
            Assert.That(InProcessLidarrGateway.IsSourceInsideScanFolder(album, Path.Combine(root, "downloads", "album-two", "01.flac")), Is.False);
            Assert.That(InProcessLidarrGateway.IsSourceInsideScanFolder(album, Path.Combine(root, "Artist", "Album", "01.flac")), Is.False);
        });
    }
}
