using Lidarr.Plugin.AIImport.Workflows;

namespace Lidarr.Plugin.AIImport.Tests;

[TestFixture]
public sealed class YoloAutomationServiceTests
{
    [Test]
    public void Identical_recovery_warning_is_suppressed_across_service_instances()
    {
        var approvalId = Guid.NewGuid().ToString("N");
        Assert.Multiple(() =>
        {
            Assert.That(YoloAutomationService.ShouldReportUnresolved(approvalId, "same failure"), Is.True);
            Assert.That(YoloAutomationService.ShouldReportUnresolved(approvalId, "same failure"), Is.False);
            Assert.That(YoloAutomationService.ShouldReportUnresolved(approvalId, "changed failure"), Is.True);
        });

        YoloAutomationService.ClearReportedUnresolved(approvalId);
        Assert.That(YoloAutomationService.ShouldReportUnresolved(approvalId, "changed failure"), Is.True);
        YoloAutomationService.ClearReportedUnresolved(approvalId);
    }
}