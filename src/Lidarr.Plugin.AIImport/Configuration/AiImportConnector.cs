using FluentValidation.Results;
using Lidarr.Plugin.AIImport.Configuration;
using Lidarr.Plugin.AIImport.Llm;
using NzbDrone.Core.Notifications;
using NzbDrone.Core.ThingiProvider;

namespace Lidarr.Plugin.AIImport.Configuration;

public sealed class AiImportConnector : NotificationBase<AiImportSettings>
{
    private readonly IOpenAiChatClient _client;

    public AiImportConnector(IOpenAiChatClient client)
    {
        _client = client;
    }

    public override string Name => "AI Import";
    public override string Link => "https://github.com/mshihadeh1/Lidarr.Plugin.AIImport";
    public override ProviderMessage Message => new(
        "Read-only preview foundation. This release configures and tests the LLM endpoint; it does not import or modify files.",
        ProviderMessageType.Info);

    public override ValidationResult Test()
    {
        try
        {
            _client.TestConnectionAsync(Settings, CancellationToken.None).GetAwaiter().GetResult();
            return new ValidationResult();
        }
        catch (Exception ex)
        {
            return new ValidationResult(new[]
            {
                new ValidationFailure(string.Empty, $"Unable to connect to the OpenAI-compatible endpoint: {Sanitize(ex.Message, Settings.ApiKey)}")
            });
        }
    }

    private static string Sanitize(string message, string apiKey)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return "unknown error";
        }

        var redacted = string.IsNullOrEmpty(apiKey)
            ? message
            : message.Replace(apiKey, "[REDACTED]", StringComparison.Ordinal);
        return redacted.Length <= 300 ? redacted : redacted[..300];
    }
}
