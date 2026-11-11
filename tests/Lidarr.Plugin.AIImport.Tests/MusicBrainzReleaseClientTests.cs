using System.Net;
using System.Text;
using Lidarr.Plugin.AIImport.Discovery;
using Lidarr.Plugin.AIImport.MusicBrainz;

namespace Lidarr.Plugin.AIImport.Tests;

[TestFixture]
public sealed class MusicBrainzReleaseClientTests
{
    [Test]
    public async Task Falls_back_to_a_normalized_release_title_with_track_count_before_giving_up()
    {
        var releaseId = Guid.NewGuid();
        var releaseGroupId = Guid.NewGuid();
        var artistId = Guid.NewGuid();
        var recordings = new[] { Guid.NewGuid(), Guid.NewGuid() };
        var queries = new List<string>();
        using var handler = new DelegateHandler(request =>
        {
            if (!request.RequestUri!.AbsolutePath.EndsWith("/release/", StringComparison.Ordinal))
            {
                return JsonResponse($$$"""
                    {
                      "id":"{{{releaseId:D}}}",
                      "title":"ROCKET",
                      "date":"2026-01-01",
                      "country":"JP",
                      "release-group":{"id":"{{{releaseGroupId:D}}}"},
                      "artist-credit":[{"artist":{"id":"{{{artistId:D}}}","name":"BURNOUT SYNDROMES"}}],
                      "media":[{"position":1,"tracks":[
                        {"position":1,"title":"One","length":123000,"recording":{"id":"{{{recordings[0]:D}}}","title":"One"}},
                        {"position":2,"title":"Two","length":124000,"recording":{"id":"{{{recordings[1]:D}}}","title":"Two"}}
                      ]}]
                    }
                    """);
            }

            var query = Uri.UnescapeDataString(request.RequestUri.Query);
            queries.Add(query);
            if (!query.Contains("release:\"ROCKET\"", StringComparison.Ordinal))
                return JsonResponse("{\"count\":0,\"releases\":[]}");
            return JsonResponse($$$"""
                {"count":1,"releases":[{"id":"{{{releaseId:D}}}","score":100,"media":[{"track-count":2}]}]}
                """);
        });
        using var http = new HttpClient(handler);
        var client = new MusicBrainzReleaseClient(http, TimeProvider.System);
        var files = new[]
        {
            Source("one", "One", 1),
            Source("two", "Two", 2)
        };

        var result = await client.SearchAsync(files, 5, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(result.Candidates, Has.Count.EqualTo(1));
            Assert.That(result.Candidates[0].ReleaseMusicBrainzId, Is.EqualTo(releaseId.ToString("D")));
            Assert.That(queries, Has.Some.Contains("tracks:2"));
            Assert.That(queries, Has.Some.Contains("release:\"ROCKET\""));
        });
    }

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

    private static SourceFileCandidate Source(string id, string title, int track) =>
        new(id, $"{track:00} - {title}.flac", "BURNOUT SYNDROMES",
            "ROCKET (2026) [WEB FLAC 24／48]", title, 1, [track], 123000, null, []);

    private static HttpResponseMessage JsonResponse(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json")
    };
}
