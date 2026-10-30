using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Lidarr.AIImport.Companion.Configuration;

namespace Lidarr.AIImport.Companion.Llm;

public interface IOpenAiChatClient
{
    Task<string> CompleteJsonAsync(string systemPrompt, string userPrompt, CancellationToken cancellationToken);
}

public sealed class LlmProtocolException : Exception
{
    public LlmProtocolException(string message) : base(message)
    {
    }
}

public sealed class OpenAiChatClient : IOpenAiChatClient
{
    private const int MaximumResponseBytes = 1_048_576;
    private const string SafetyRules =
        "Select only identifiers present in the supplied verified candidates. " +
        "Never invent or alter identifiers. Return manualReview when the candidates do not establish a reliable exhaustive match. " +
        "Your response is an untrusted proposal and cannot authorize an import.";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };
    private readonly HttpClient _httpClient;
    private readonly CompanionOptions _options;

    public OpenAiChatClient(HttpClient httpClient, CompanionOptions options)
    {
        _httpClient = httpClient;
        _httpClient.Timeout = Timeout.InfiniteTimeSpan;
        _options = options;
    }

    public async Task<string> CompleteJsonAsync(string systemPrompt, string userPrompt, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(_options.LlmTimeoutSeconds));
        var endpoint = _options.LlmBaseUrl.Trim().TrimEnd('/');
        if (!endpoint.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase))
            endpoint += "/chat/completions";

        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
        if (!string.IsNullOrWhiteSpace(_options.LlmApiKey))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.LlmApiKey);
        request.Content = JsonContent.Create(new ChatRequest(
            _options.LlmModel,
            _options.LlmTemperature,
            _options.LlmMaximumOutputTokens,
            [new ChatMessage("system", SafetyRules + "\n\n" + systemPrompt), new ChatMessage("user", userPrompt)]),
            options: JsonOptions);

        using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"LLM endpoint returned HTTP {(int)response.StatusCode} ({response.ReasonPhrase}).");
        var payload = await ReadBoundedAsync(response.Content, timeout.Token).ConfigureAwait(false);
        try
        {
            using var document = JsonDocument.Parse(payload);
            return document.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString()
                ?? throw new LlmProtocolException("The LLM response content was null.");
        }
        catch (LlmProtocolException)
        {
            throw;
        }
        catch (Exception exception) when (exception is JsonException or KeyNotFoundException or InvalidOperationException or IndexOutOfRangeException)
        {
            throw new LlmProtocolException("The endpoint returned a malformed chat-completion response.");
        }
    }

    private static async Task<string> ReadBoundedAsync(HttpContent content, CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength > MaximumResponseBytes)
            throw new LlmProtocolException("The endpoint response exceeded the maximum allowed size.");
        await using var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var output = new MemoryStream();
        var buffer = new byte[8192];
        while (true)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            if (output.Length + read > MaximumResponseBytes)
                throw new LlmProtocolException("The endpoint response exceeded the maximum allowed size.");
            output.Write(buffer, 0, read);
        }
        return Encoding.UTF8.GetString(output.GetBuffer(), 0, checked((int)output.Length));
    }

    private sealed record ChatRequest(
        [property: JsonPropertyName("model")] string Model,
        [property: JsonPropertyName("temperature")] decimal Temperature,
        [property: JsonPropertyName("max_tokens")] int MaximumOutputTokens,
        [property: JsonPropertyName("messages")] IReadOnlyList<ChatMessage> Messages);
    private sealed record ChatMessage(
        [property: JsonPropertyName("role")] string Role,
        [property: JsonPropertyName("content")] string Content);
}
