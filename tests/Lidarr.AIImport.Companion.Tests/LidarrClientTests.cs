using System.Net;
using System.Text;
using Lidarr.AIImport.Companion.Configuration;
using Lidarr.AIImport.Companion.Lidarr;

namespace Lidarr.AIImport.Companion.Tests;

[TestFixture]
public sealed class LidarrClientTests
{
    [Test]
    public async Task Discovery_uses_only_get_requests_and_api_key_header()
    {
        var handler = new FixtureHandler();
        var client = new LidarrClient(new HttpClient(handler), new CompanionOptions
        {
            LidarrBaseUrl = "http://lidarr:8686",
            LidarrApiKey = "test-key",
            LlmBaseUrl = "http://llm:8080/v1",
            LlmModel = "model"
        });

        var status = await client.GetSystemStatusAsync(CancellationToken.None);
        var queue = await client.GetQueueAsync(CancellationToken.None);
        var files = await client.GetManualImportsAsync("download id", null, CancellationToken.None);
        var sameFiles = await client.GetManualImportsAsync("download id", null, CancellationToken.None);
        var otherDownloadFiles = await client.GetManualImportsAsync("other download", null, CancellationToken.None);
        var releases = await client.GetReleaseCandidatesAsync(10, CancellationToken.None);
        var safety = await client.GetImportSafetyConfigurationAsync(CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(status.Version, Is.EqualTo("3.1.2.4913"));
            Assert.That(queue, Has.Count.EqualTo(1));
            Assert.That(files, Has.Count.EqualTo(1));
            Assert.That(files.Single().ArtistId, Is.EqualTo(5));
            Assert.That(files.Single().AlbumId, Is.EqualTo(10));
            Assert.That(files.Single().Id, Has.Length.EqualTo(20));
            Assert.That(files.Single().Id, Is.EqualTo(sameFiles.Single().Id));
            Assert.That(files.Single().Id, Is.Not.EqualTo(otherDownloadFiles.Single().Id));
            Assert.That(files.Single().Id, Does.Not.Contain("downloads"));
            Assert.That(releases.Single().Tracks, Has.Count.EqualTo(1));
            Assert.That(safety.CopyUsingHardlinks, Is.True);
            Assert.That(safety.UseScriptImport, Is.True);
            Assert.That(safety.ScriptImportPath, Is.EqualTo("/config/lidarr-ai-import/lidarr-hardlink-import"));
            Assert.That(safety.SetPermissionsLinux, Is.False);
            Assert.That(safety.FileDate, Is.EqualTo("none"));
            Assert.That(safety.WriteAudioTags, Is.EqualTo("no"));
            Assert.That(handler.Requests, Has.All.Matches<HttpRequestMessage>(r => r.Method == HttpMethod.Get));
            Assert.That(handler.Requests, Has.All.Matches<HttpRequestMessage>(r => r.Headers.GetValues("X-Api-Key").Single() == "test-key"));
            Assert.That(handler.Requests.Any(r => r.RequestUri!.Query.Contains("downloadId=download%20id")), Is.True);
            Assert.That(handler.Requests.Any(r => r.RequestUri!.AbsolutePath == "/api/v1/track" && r.RequestUri.Query == "?albumReleaseId=101"), Is.True);
        });
    }

    [Test]
    public async Task Queue_discovery_reads_every_reported_page()
    {
        var handler = new PagingHandler();
        var client = new LidarrClient(new HttpClient(handler), new CompanionOptions
        {
            LidarrBaseUrl = "http://lidarr:8686",
            LidarrApiKey = "test-key"
        });

        var queue = await client.GetQueueAsync(CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(queue.Select(x => x.DownloadId), Is.EqualTo(new[] { "first", "second" }));
            Assert.That(handler.Pages, Is.EqualTo(new[] { 1, 2 }));
        });
    }

    [Test]
    public async Task Queue_pagination_counts_rows_without_download_ids()
    {
        var handler = new SkippedRowHandler();
        var client = new LidarrClient(new HttpClient(handler), new CompanionOptions
        {
            LidarrBaseUrl = "http://lidarr:8686",
            LidarrApiKey = "test-key"
        });

        var queue = await client.GetQueueAsync(CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(queue.Select(x => x.DownloadId), Is.EqualTo(new[] { "usable" }));
            Assert.That(handler.RequestCount, Is.EqualTo(1));
        });
    }

    [TestCase("missing-release-id")]
    [TestCase("wrong-release-album")]
    [TestCase("missing-track-id")]
    [TestCase("wrong-track-album")]
    [TestCase("missing-has-file")]
    public void Release_candidates_reject_malformed_or_unrelated_ids(string fault)
    {
        var client = new LidarrClient(new HttpClient(new InvalidReleaseHandler(fault)), new CompanionOptions
        {
            LidarrBaseUrl = "http://lidarr:8686",
            LidarrApiKey = "test-key"
        });

        Assert.ThrowsAsync<InvalidDataException>(() =>
            client.GetReleaseCandidatesAsync(10, CancellationToken.None));
    }

    [Test]
    public void Manual_imports_reject_duplicate_source_identities()
    {
        var client = new LidarrClient(new HttpClient(new DuplicateManualImportHandler()), new CompanionOptions
        {
            LidarrBaseUrl = "http://lidarr:8686",
            LidarrApiKey = "test-key"
        });

        Assert.ThrowsAsync<InvalidDataException>(() =>
            client.GetManualImportsAsync("download", null, CancellationToken.None));
    }

    [TestCase("fileDate")]
    [TestCase("writeAudioTags")]
    public void Import_safety_rejects_numeric_enum_values(string fault)
    {
        var client = new LidarrClient(new HttpClient(new InvalidSafetyConfigurationHandler(fault)), new CompanionOptions
        {
            LidarrBaseUrl = "http://lidarr:8686",
            LidarrApiKey = "test-key"
        });

        Assert.ThrowsAsync<InvalidDataException>(() =>
            client.GetImportSafetyConfigurationAsync(CancellationToken.None));
    }

    private sealed class FixtureHandler : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            var path = request.RequestUri!.AbsolutePath;
            var json = path switch
            {
                "/api/v1/system/status" => """{"version":"3.1.2.4913","packageVersion":"3.1.2.4913","branch":"plugins"}""",
                "/api/v1/queue" => """{"page":1,"pageSize":100,"totalRecords":1,"records":[{"id":1,"albumId":10,"title":"Album","status":"completed","trackedDownloadState":"importPending","statusMessages":[],"downloadId":"download id","outputPath":"/downloads/album"}]}""",
                "/api/v1/manualimport" => """[{"id":1,"path":"/downloads/album/01.flac","name":"01.flac","size":100,"artist":{"id":5,"artistName":"Artist"},"album":{"id":10,"title":"Album"},"albumReleaseId":101,"tracks":[],"rejections":[],"audioTags":{"title":"Song","artistTitle":"Artist","albumTitle":"Album","discNumber":1,"trackNumbers":[1],"duration":"00:03:00"}}]""",
                "/api/v1/album/10" => """{"id":10,"title":"Album","artist":{"id":5,"artistName":"Artist"},"releases":[{"id":101,"albumId":10,"foreignReleaseId":"release-101","title":"Album","trackCount":1,"disambiguation":"","monitored":true}]}""",
                "/api/v1/track" => """[{"id":1001,"albumId":10,"foreignTrackId":"track-1","foreignRecordingId":"recording-1","trackNumber":"1","title":"Song","duration":180000,"mediumNumber":1,"hasFile":false}]""",
                "/api/v1/config/mediamanagement" => """{"copyUsingHardlinks":true,"useScriptImport":true,"scriptImportPath":"/config/lidarr-ai-import/lidarr-hardlink-import","setPermissionsLinux":false,"fileDate":"none","importExtraFiles":false}""",
                "/api/v1/config/metadataprovider" => """{"writeAudioTags":"no","scrubAudioTags":false,"embedCoverArt":false}""",
                _ => throw new AssertionException($"Unexpected request: {request.RequestUri}")
            };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            });
        }
    }

    private sealed class InvalidReleaseHandler(string fault) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var isAlbum = request.RequestUri!.AbsolutePath.StartsWith("/api/v1/album/", StringComparison.Ordinal);
            var releaseId = fault == "missing-release-id" ? "" : "\"id\":101,";
            var releaseAlbumId = fault == "wrong-release-album" ? 99 : 10;
            var trackId = fault == "missing-track-id" ? "" : "\"id\":1001,";
            var trackAlbumId = fault == "wrong-track-album" ? 99 : 10;
            var hasFile = fault == "missing-has-file" ? "" : ",\"hasFile\":false";
            var json = isAlbum
                ? $$"""{"id":10,"title":"Album","artist":{"id":5,"artistName":"Artist"},"releases":[{ {{releaseId}} "albumId":{{releaseAlbumId}},"title":"Album"}]}"""
                : $$"""[{ {{trackId}} "albumId":{{trackAlbumId}},"title":"Song","trackNumber":"1","mediumNumber":1,"duration":180000{{hasFile}}}]""";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            });
        }
    }

    private sealed class DuplicateManualImportHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            const string item = """{"path":"/downloads/album/01.flac","name":"01.flac","rejections":[],"audioTags":{"title":"Song"}}""";
            var json = $"[{item},{item}]";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            });
        }
    }

    private sealed class InvalidSafetyConfigurationHandler(string fault) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var media = request.RequestUri!.AbsolutePath.EndsWith("mediamanagement", StringComparison.Ordinal);
            var fileDate = fault == "fileDate" ? "0" : "\"none\"";
            var writeAudioTags = fault == "writeAudioTags" ? "0" : "\"no\"";
            var json = media
                ? $$"""{"copyUsingHardlinks":true,"useScriptImport":true,"scriptImportPath":"/script","setPermissionsLinux":false,"fileDate":{{fileDate}},"importExtraFiles":false}"""
                : $$"""{"writeAudioTags":{{writeAudioTags}},"scrubAudioTags":false,"embedCoverArt":false}""";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            });
        }
    }

    private sealed class SkippedRowHandler : HttpMessageHandler
    {
        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            const string json = """{"page":1,"pageSize":100,"totalRecords":2,"records":[{"id":1,"title":"No ID","status":"completed","trackedDownloadState":"importFailed","downloadId":"","statusMessages":[]},{"id":2,"title":"Usable","status":"completed","trackedDownloadState":"importFailed","downloadId":"usable","statusMessages":[]}]}""";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            });
        }
    }

    private sealed class PagingHandler : HttpMessageHandler
    {
        public List<int> Pages { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var query = request.RequestUri!.Query;
            var page = query.Contains("page=1&", StringComparison.Ordinal) ? 1 : 2;
            Pages.Add(page);
            var downloadId = page == 1 ? "first" : "second";
            var id = page;
            var json = $$"""{"page":{{page}},"pageSize":100,"totalRecords":2,"records":[{"id":{{id}},"title":"Album","status":"completed","trackedDownloadState":"importFailed","downloadId":"{{downloadId}}","statusMessages":[]}]}""";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            });
        }
    }
}
