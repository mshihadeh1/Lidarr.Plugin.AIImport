using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Lidarr.Plugin.AIImport.Configuration;
using Lidarr.Plugin.AIImport.Llm;

namespace Lidarr.Plugin.AIImport.Tests;

[TestFixture]
public sealed class OpenAiChatClientTests
{
    [Test]
    public async Task TestConnection_posts_to_chat_completions_and_accepts_optional_key()
    {
        var handler = new RecordingHandler("""{"choices":[{"message":{"content":"AI_IMPORT_OK"}}]}""");
        var client = new OpenAiChatClient(new HttpClient(handler));
        var settings = ValidSettings();
        settings.ApiKey = string.Empty;

        var result = await client.TestConnectionAsync(settings, CancellationToken.None);

        Assert.That(result.Success, Is.True);
        Assert.That(handler.Request!.RequestUri!.ToString(), Is.EqualTo("http://llm.local/v1/chat/completions"));
        Assert.That(handler.Request.Headers.Authorization, Is.Null);
        Assert.That(handler.Body, Does.Contain("test-model"));
    }

    [Test]
    public async Task TestConnection_sends_bearer_key_when_configured()
    {
        var handler = new RecordingHandler("""{"choices":[{"message":{"content":"AI_IMPORT_OK"}}]}""");
        var client = new OpenAiChatClient(new HttpClient(handler));
        var settings = ValidSettings();
        settings.ApiKey = "secret";

        await client.TestConnectionAsync(settings, CancellationToken.None);

        Assert.That(handler.Request!.Headers.Authorization, Is.EqualTo(new AuthenticationHeaderValue("Bearer", "secret")));
    }

    [Test]
    public void TestConnection_rejects_unexpected_model_response()
    {
        var handler = new RecordingHandler("""{"choices":[{"message":{"content":"something else"}}]}""");
        var client = new OpenAiChatClient(new HttpClient(handler));

        Assert.ThrowsAsync<LlmProtocolException>(() => client.TestConnectionAsync(ValidSettings(), CancellationToken.None));
    }

    [Test]
    public void Completion_rejects_oversized_response_before_buffering()
    {
        var handler = new RecordingHandler(new string('x', 1_048_577));
        var client = new OpenAiChatClient(new HttpClient(handler));

        var error = Assert.ThrowsAsync<LlmProtocolException>(() =>
            client.CompleteJsonAsync(ValidSettings(), "system", "user", CancellationToken.None));
        Assert.That(error!.Message, Does.Contain("maximum allowed size"));
    }

    [Test]
    public void TestConnection_normalizes_full_chat_completions_url()
    {
        var handler = new RecordingHandler("""{"choices":[{"message":{"content":"AI_IMPORT_OK"}}]}""");
        var client = new OpenAiChatClient(new HttpClient(handler));
        var settings = ValidSettings();
        settings.ApiBaseUrl = "http://llm.local/v1/chat/completions/";

        Assert.DoesNotThrowAsync(() => client.TestConnectionAsync(settings, CancellationToken.None));
        Assert.That(handler.Request!.RequestUri!.ToString(), Is.EqualTo("http://llm.local/v1/chat/completions"));
    }

    [Test]
    public async Task Completion_places_candidate_safety_rules_before_operator_instructions()
    {
        var handler = new RecordingHandler("""{"choices":[{"message":{"content":"{}"}}]}""");
        var client = new OpenAiChatClient(new HttpClient(handler));
        var settings = ValidSettings();
        settings.CustomSystemPrompt = "Explain uncertainty briefly.";

        await client.CompleteJsonAsync(settings, "Rank the supplied candidates.", "candidate payload", CancellationToken.None);

        Assert.That(handler.Body, Does.Contain("Never invent or alter identifiers"));
        Assert.That(handler.Body, Does.Contain("Explain uncertainty briefly."));
    }

    private static AiImportSettings ValidSettings() => new()
    {
        ApiBaseUrl = "http://llm.local/v1/",
        Model = "test-model",
        Temperature = 0.1m,
        RequestTimeoutSeconds = 30,
        MaximumOutputTokens = 500
    };

    private sealed class RecordingHandler(string response) : HttpMessageHandler
    {
        public HttpRequestMessage? Request { get; private set; }
        public string Body { get; private set; } = string.Empty;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Request = request;
            Body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(response, Encoding.UTF8, "application/json")
            };
        }
    }
}
