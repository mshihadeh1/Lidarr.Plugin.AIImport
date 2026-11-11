using System.Net;
using System.Text;
using Lidarr.Plugin.AIImport.MusicBrainz;

namespace Lidarr.Plugin.AIImport.Tests;

[TestFixture]
public sealed class MusicBrainzReleaseClientTests
{
    [Test]
    public async Task Retries_one_transient_service_failure_then_returns_the_authoritative_release()
    {
        var releaseId = Guid.NewGuid();
        var releaseGroupId = Guid.NewGuid();
        var artistId = Guid.NewGuid();
        var recordingId = Guid.NewGuid();
        var calls = 0;
        using var handler = new DelegateHandler(_ =>
        {
            calls++;
            if (calls == 1) return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
            var json = $$$"""
                {
                  "id":"{{{releaseId:D}}}",
                  "title":"Acceptance Album",
                  "date":"2026-01-01",
                  "country":"US",
                  "release-group":{"id":"{{{releaseGroupId:D}}}"},
                  "artist-credit":[{"artist":{"id":"{{{artistId:D}}}","name":"Acceptance Artist"}}],
                  "media":[{"position":1,"tracks":[{"position":1,"title":"Track One","length":123000,"recording":{"id":"{{{recordingId:D}}}","title":"Track One"}}]}]
                }
                """;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
        });
        using var http = new HttpClient(handler);
        var client = new MusicBrainzReleaseClient(http, TimeProvider.System);

        var result = await client.GetAsync(releaseId, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(calls, Is.EqualTo(2));
            Assert.That(result, Is.Not.Null);
            Assert.That(result!.ReleaseMusicBrainzId, Is.EqualTo(releaseId.ToString("D")));
            Assert.That(result.Tracks, Has.Count.EqualTo(1));
        });
    }

    private sealed class DelegateHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }
}
