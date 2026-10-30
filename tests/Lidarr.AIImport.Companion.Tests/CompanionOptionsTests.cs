using Lidarr.AIImport.Companion.Configuration;

namespace Lidarr.AIImport.Companion.Tests;

[TestFixture]
public sealed class CompanionOptionsTests
{
    [Test]
    public void Discovery_requires_an_absolute_transfer_authorization_directory()
    {
        var options = new CompanionOptions
        {
            LidarrBaseUrl = "http://lidarr:8686",
            LidarrApiKey = "lidarr-key",
            CompanionApiToken = "companion-key",
            NativeHelperPath = "/app/lidarr-hardlink-import",
            ApprovalStorePath = "/data/approvals.json",
            AuthorizationDirectory = "relative"
        };

        Assert.Throws<InvalidOperationException>(options.ValidateDiscovery);
    }
}
