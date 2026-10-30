using System.Net;
using System.Text;
using Lidarr.AIImport.Companion.Configuration;
using Lidarr.AIImport.Companion.Llm;

namespace Lidarr.AIImport.Companion.Tests;

[TestFixture]
public sealed class OpenAiChatClientTests
{
    [Test]
    public async Task Sends_bounded_candidate_prompt_and_returns_message_content()
    {
        var handler = new CapturingHandler("""{"choices":[{"message":{"content":"{\"schemaVersion\":1}"}}]}""");
        var client = new OpenAiChatClient(new HttpClient(handler), Options());

        var result = await client.CompleteJsonAsync("system safety", "candidate payload", CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(result, Is.EqualTo("{\"schemaVersion\":1}"));
            Assert.That(handler.RequestUri, Is.EqualTo("http://model.local/v1/chat/completions"));
            Assert.That(handler.Authorization, Is.EqualTo("Bearer test-token"));
            Assert.That(handler.Body, Does.Contain("system safety"));
            Assert.That(handler.Body, Does.Contain("candidate payload"));
            Assert.That(handler.Body, Does.Contain("Never invent"));
        });
    }

    [Test]
    public void Rejects_malformed_protocol_response()
    {
        var client = new OpenAiChatClient(new HttpClient(new CapturingHandler("{}")), Options());
        Assert.ThrowsAsync<LlmProtocolException>(() =>
            client.CompleteJsonAsync("system", "user", CancellationToken.None));
    }

    private static CompanionOptions Options() => new()
    {
        LidarrBaseUrl = "http://lidarr.local",
        LidarrApiKey = "lidarr-key",
        CompanionApiToken = "companion-token",
        LlmBaseUrl = "http://model.local/v1",
        LlmApiKey = "test-token",
        LlmModel = "local-model",
        LlmTemperature = 0.1m,
        LlmTimeoutSeconds = 10,
        LlmMaximumOutputTokens = 1000
    };

    private sealed class CapturingHandler(string responseBody) : HttpMessageHandler
    {
        public string? RequestUri { get; private set; }
        public string? Authorization { get; private set; }
        public string? Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestUri = request.RequestUri?.ToString();
            Authorization = request.Headers.Authorization?.ToString();
            Body = await request.Content!.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responseBody, Encoding.UTF8, "application/json")
            };
        }
    }
}
