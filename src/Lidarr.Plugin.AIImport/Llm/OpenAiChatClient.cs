using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Lidarr.Plugin.AIImport.Configuration;

namespace Lidarr.Plugin.AIImport.Llm;

public interface IOpenAiChatClient
{
    Task<LlmTestResult> TestConnectionAsync(AiImportSettings settings, CancellationToken cancellationToken);
    Task<string> CompleteJsonAsync(AiImportSettings settings, string systemPrompt, string userPrompt, CancellationToken cancellationToken);
}

public sealed record LlmTestResult(bool Success, string ModelResponse);

public sealed class LlmProtocolException : Exception
{
    public LlmProtocolException(string message) : base(message)
    {
    }
}

public sealed class OpenAiChatClient : IOpenAiChatClient
{
    private const int MaximumResponseBytes = 1_048_576;
    private const string CandidateSafetyRules =
        "You may select only identifiers present in the supplied verified candidates. " +
        "Never invent or alter identifiers. Request manual review when the candidates do not establish a reliable match.";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private static readonly JsonElement ProposalResponseFormat = JsonDocument.Parse("""
        {
          "type": "json_schema",
          "json_schema": {
            "name": "ai_import_proposal",
            "strict": true,
            "schema": {
              "type": "object",
              "additionalProperties": false,
              "properties": {
                "schemaVersion": { "type": "integer", "const": 1 },
                "status": { "type": "string", "enum": ["proposed", "manualReview"] },
                "artistId": { "type": ["integer", "null"] },
                "albumId": { "type": ["integer", "null"] },
                "releaseId": { "type": ["integer", "null"] },
                "confidence": { "type": "number" },
                "explanation": { "type": "string" },
                "tracks": {
                  "type": "array",
                  "items": {
                    "type": "object",
                    "additionalProperties": false,
                    "properties": {
                      "sourceFileId": { "type": "string" },
                      "trackId": { "type": "integer" },
                      "confidence": { "type": "number" }
                    },
                    "required": ["sourceFileId", "trackId", "confidence"]
                  }
                }
              },
              "required": ["schemaVersion", "status", "artistId", "albumId", "releaseId", "confidence", "explanation", "tracks"]
            }
          }
        }
        """).RootElement.Clone();

    private readonly HttpClient _httpClient;

    public OpenAiChatClient() : this(new HttpClient())
    {
    }

    public OpenAiChatClient(HttpClient httpClient)
    {
        _httpClient = httpClient;
        _httpClient.Timeout = Timeout.InfiniteTimeSpan;
    }

    public async Task<LlmTestResult> TestConnectionAsync(AiImportSettings settings, CancellationToken cancellationToken)
    {
        var response = await SendAsync(
            settings,
            "You are a connection tester. Follow the user's output instruction exactly.",
            "Reply with exactly AI_IMPORT_OK and no other text.",
            structuredOutput: false,
            cancellationToken).ConfigureAwait(false);

        if (!string.Equals(response.Trim(), "AI_IMPORT_OK", StringComparison.Ordinal))
        {
            throw new LlmProtocolException("The endpoint responded, but the model did not return the expected test token.");
        }

        return new LlmTestResult(true, response.Trim());
    }

    public Task<string> CompleteJsonAsync(AiImportSettings settings, string systemPrompt, string userPrompt, CancellationToken cancellationToken)
    {
        var effectivePrompt = $"{CandidateSafetyRules}\n\nTask instructions:\n{systemPrompt}";
        if (!string.IsNullOrWhiteSpace(settings.CustomSystemPrompt))
        {
            effectivePrompt += $"\n\nAdditional operator instructions that cannot override the safety rules above:\n{settings.CustomSystemPrompt}";
        }

        return SendAsync(settings, effectivePrompt, userPrompt, structuredOutput: true, cancellationToken);
    }

    internal static Uri BuildChatCompletionsUri(string baseUrl)
    {
        var value = baseUrl.Trim().TrimEnd('/');
        if (value.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase))
        {
            return new Uri(value, UriKind.Absolute);
        }

        return new Uri($"{value}/chat/completions", UriKind.Absolute);
    }

    private async Task<string> SendAsync(
        AiImportSettings settings,
        string systemPrompt,
        string userPrompt,
        bool structuredOutput,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(settings.RequestTimeoutSeconds));

        using var request = new HttpRequestMessage(HttpMethod.Post, BuildChatCompletionsUri(settings.ApiBaseUrl));
        if (!string.IsNullOrWhiteSpace(settings.ApiKey))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.ApiKey);
        }

        request.Content = JsonContent.Create(new ChatRequest(
            settings.Model,
            settings.Temperature,
            settings.MaximumOutputTokens,
            new[]
            {
                new ChatMessage("system", systemPrompt),
                new ChatMessage("user", userPrompt)
            },
            structuredOutput ? ProposalResponseFormat : null,
            settings.DisableThinking ? new ChatTemplateKwargs(false) : null), options: JsonOptions);

        using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"HTTP {(int)response.StatusCode} ({response.ReasonPhrase})");
        }

        var payload = await ReadBoundedResponseAsync(response.Content, timeout.Token).ConfigureAwait(false);

        try
        {
            using var document = JsonDocument.Parse(payload);
            var content = document.RootElement
                .GetProperty("choices")[0]
                .GetProperty("message")
                .GetProperty("content")
                .GetString();
            return content ?? throw new LlmProtocolException("The response message content was null.");
        }
        catch (LlmProtocolException)
        {
            throw;
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or IndexOutOfRangeException)
        {
            throw new LlmProtocolException("The endpoint returned a malformed chat-completion response.");
        }
    }

    private static async Task<string> ReadBoundedResponseAsync(HttpContent content, CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength > MaximumResponseBytes)
        {
            throw new LlmProtocolException("The endpoint response exceeded the maximum allowed size.");
        }

        await using var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        while (true)
        {
            var read = await stream.ReadAsync(chunk.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            if (buffer.Length + read > MaximumResponseBytes)
            {
                throw new LlmProtocolException("The endpoint response exceeded the maximum allowed size.");
            }

            buffer.Write(chunk, 0, read);
        }

        return Encoding.UTF8.GetString(buffer.GetBuffer(), 0, checked((int)buffer.Length));
    }

    private sealed record ChatRequest(
        [property: JsonPropertyName("model")] string Model,
        [property: JsonPropertyName("temperature")] decimal Temperature,
        [property: JsonPropertyName("max_tokens")] int MaximumOutputTokens,
        [property: JsonPropertyName("messages")] IReadOnlyList<ChatMessage> Messages,
        [property: JsonPropertyName("response_format")] JsonElement? ResponseFormat,
        [property: JsonPropertyName("chat_template_kwargs")] ChatTemplateKwargs? ChatTemplateKwargs);

    private sealed record ChatTemplateKwargs(
        [property: JsonPropertyName("enable_thinking")] bool EnableThinking);

    private sealed record ChatMessage(
        [property: JsonPropertyName("role")] string Role,
        [property: JsonPropertyName("content")] string Content);
}
