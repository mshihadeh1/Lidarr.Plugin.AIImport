using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Lidarr.AIImport.Companion.Approval;
using Lidarr.AIImport.Companion.Configuration;
using Lidarr.AIImport.Companion.Filesystem;
using Lidarr.AIImport.Companion.Lidarr;

namespace Lidarr.AIImport.Companion.Tests;

[TestFixture]
public sealed class LidarrExecutionClientTests
{
    [Test]
    public async Task Submits_exact_copy_mode_manual_import_without_replace_fallback_flags()
    {
        var handler = new CaptureHandler();
        var client = new LidarrClient(new HttpClient(handler), new CompanionOptions
        {
            LidarrBaseUrl = "http://lidarr:8686",
            LidarrApiKey = "key"
        });
        var plan = new ApprovedImportPlan(
            "analysis", "download", 5, 10, 101, "safety",
            [new ApprovedImportFile(
                "source", 77, "/downloads/track.flac", 1001,
                JsonNode.Parse("{\"quality\":{\"id\":1,\"name\":\"FLAC\"},\"revision\":{\"version\":1,\"real\":0,\"isRepack\":false}}")!.AsObject(),
                0, "download", 123, new FileIdentity(1, 2, 123, 4, 5, 8, 9, 420, 6, 7, 1))]);

        var receipt = await client.SubmitManualImportAsync(plan, CancellationToken.None);

        Assert.That(receipt.CommandId, Is.EqualTo(42));
        using var body = JsonDocument.Parse(handler.Body!);
        var root = body.RootElement;
        var file = root.GetProperty("files")[0];
        Assert.Multiple(() =>
        {
            Assert.That(root.GetProperty("name").GetString(), Is.EqualTo("ManualImport"));
            Assert.That(root.GetProperty("importMode").GetString(), Is.EqualTo("copy"));
            Assert.That(root.GetProperty("replaceExistingFiles").GetBoolean(), Is.False);
            Assert.That(file.GetProperty("artistId").GetInt32(), Is.EqualTo(5));
            Assert.That(file.GetProperty("albumReleaseId").GetInt32(), Is.EqualTo(101));
            Assert.That(file.GetProperty("trackIds")[0].GetInt32(), Is.EqualTo(1001));
            Assert.That(file.GetProperty("disableReleaseSwitching").GetBoolean(), Is.False);
        });
    }

    [Test]
    public async Task Revalidates_selected_mapping_through_lidarr_manual_import_decisions()
    {
        const string response = """[{"id":77,"path":"/downloads/track.flac","name":"track.flac","size":123,"artist":{"id":5},"album":{"id":10},"albumReleaseId":101,"tracks":[{"id":1001}],"quality":{"quality":{"id":1}},"downloadId":"download","indexerFlags":0,"rejections":[],"additionalFile":false,"replaceExistingFiles":false,"disableReleaseSwitching":false}]""";
        var handler = new ManualImportHandler(response);
        var client = new LidarrClient(new HttpClient(handler), new CompanionOptions
        {
            LidarrBaseUrl = "http://lidarr:8686",
            LidarrApiKey = "key"
        });
        var plan = new ApprovedImportPlan(
            "analysis", "download", 5, 10, 101, "safety",
            [new ApprovedImportFile(
                "source", 77, "/downloads/track.flac", 1001,
                JsonNode.Parse("{\"quality\":{\"id\":1}}")!.AsObject(), 0, "download", 123,
                new FileIdentity(1, 2, 123, 4, 5, 8, 9, 420, 6, 7, 1))]);

        await client.RevalidateManualImportAsync(plan, CancellationToken.None);

        Assert.That(handler.Path, Is.EqualTo("/api/v1/manualimport"));
        using var body = JsonDocument.Parse(handler.Body!);
        Assert.Multiple(() =>
        {
            Assert.That(body.RootElement[0].GetProperty("id").GetInt32(), Is.EqualTo(77));
            Assert.That(body.RootElement[0].GetProperty("albumReleaseId").GetInt32(), Is.EqualTo(101));
            Assert.That(body.RootElement[0].GetProperty("replaceExistingFiles").GetBoolean(), Is.False);
        });
    }

    [Test]
    public void Revalidation_rejects_changed_quality()
    {
        const string response = """[{"id":77,"path":"/downloads/track.flac","size":123,"artist":{"id":5},"album":{"id":10},"albumReleaseId":101,"tracks":[{"id":1001}],"quality":{"quality":{"id":9}},"downloadId":"download","indexerFlags":0,"rejections":[],"additionalFile":false,"replaceExistingFiles":false,"disableReleaseSwitching":false}]""";
        var client = new LidarrClient(new HttpClient(new ManualImportHandler(response)), new CompanionOptions
        {
            LidarrBaseUrl = "http://lidarr:8686",
            LidarrApiKey = "key"
        });
        var plan = new ApprovedImportPlan(
            "analysis", "download", 5, 10, 101, "safety",
            [new ApprovedImportFile("source", 77, "/downloads/track.flac", 1001,
                JsonNode.Parse("{\"quality\":{\"id\":1}}")!.AsObject(), 0, "download", 123,
                new FileIdentity(1, 2, 123, 4, 5, 8, 9, 420, 6, 7, 1))]);

        Assert.ThrowsAsync<InvalidDataException>(() =>
            client.RevalidateManualImportAsync(plan, CancellationToken.None));
    }

    private sealed class ManualImportHandler(string response) : HttpMessageHandler
    {
        public string? Body { get; private set; }
        public string? Path { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Path = request.RequestUri!.AbsolutePath;
            Body = await request.Content!.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.Accepted) { Content = new StringContent(response) };
        }
    }

    private sealed class CaptureHandler : HttpMessageHandler
    {
        public string? Body { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Body = await request.Content!.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.Created)
            {
                Content = new StringContent("{\"id\":42,\"status\":\"queued\"}")
            };
        }
    }
}
