using FluentValidation.Results;
using Lidarr.Plugin.AIImport.Llm;
using Lidarr.Plugin.AIImport.Workflows;
using NzbDrone.Core.Notifications;
using NzbDrone.Core.ThingiProvider;

namespace Lidarr.Plugin.AIImport.Configuration;

public sealed class AiImportConnector : NotificationBase<AiImportSettings>
{
    private readonly IOpenAiChatClient _client;
    private readonly IAiImportDiscoverWorkflow? _workflow;
    private readonly IStandaloneAiImportWorkflow? _standaloneWorkflow;
    private readonly IAiImportSettingsResolver? _settingsResolver;

    public AiImportConnector(IOpenAiChatClient client)
    {
        _client = client;
    }

    public AiImportConnector(
        IOpenAiChatClient client,
        IAiImportDiscoverWorkflow workflow,
        IAiImportSettingsResolver settingsResolver)
    {
        _client = client;
        _workflow = workflow;
        _settingsResolver = settingsResolver;
    }

    public AiImportConnector(
        IOpenAiChatClient client,
        IAiImportDiscoverWorkflow workflow,
        IStandaloneAiImportWorkflow standaloneWorkflow,
        IAiImportSettingsResolver settingsResolver)
        : this(client, workflow, settingsResolver)
    {
        _standaloneWorkflow = standaloneWorkflow;
    }

    public override string Name => "AI Import";
    public override string Link => "https://github.com/mshihadeh1/Lidarr.Plugin.AIImport";
    public override ProviderMessage Message => new(
        "Standalone approval-gated strict-hardlink import for Lidarr 3.1.2.4913. Review proposals before approving.",
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

    public override object RequestAction(string action, IDictionary<string, string> query)
    {
        if (action == "startOAuth")
            return new { oauthUrl = WorkspaceUrl(query) };
        if (action == "getOAuthToken")
            return new Dictionary<string, string>();

        var allowed = action is "discover" or "approve" or "execute" or "reconcile" or "status";
        if (!allowed)
        {
            throw new ArgumentException("Unknown AI Import action.", nameof(action));
        }

        if (_workflow is null || _settingsResolver is null)
        {
            throw new InvalidOperationException("Standalone discovery services are unavailable.");
        }

        // Definition and query are request-controlled on the provider action route.
        // Only persisted, enabled settings are allowed to reach the workflow or LLM.
        var savedSettings = _settingsResolver.ResolveEnabled(Definition.Id);
        if (action == "discover") return _workflow.Discover(savedSettings);
        if (_standaloneWorkflow is null)
            throw new InvalidOperationException("Standalone approval and execution services are unavailable.");
        return action switch
        {
            "approve" => _standaloneWorkflow.Approve(RequiredQuery(query, "analysisId")),
            "execute" => _standaloneWorkflow.Execute(
                RequiredQuery(query, "approvalId"), RequiredQuery(query, "executeConfirmation")),
            "reconcile" => _standaloneWorkflow.Reconcile(RequiredQuery(query, "approvalId")),
            "status" => _standaloneWorkflow.Status(RequiredQuery(query, "approvalId")),
            _ => throw new ArgumentException("Unknown AI Import action.", nameof(action))
        };
    }

    private string WorkspaceUrl(IDictionary<string, string> query)
    {
        if (Definition.Id <= 0)
            throw new InvalidOperationException("Save the AI Import provider before opening its workspace.");
        var callbackUrl = RequiredQuery(query, "callbackUrl");
        if (!Uri.TryCreate(callbackUrl, UriKind.Absolute, out var callback) ||
            callback.Scheme is not ("http" or "https") ||
            !callback.AbsolutePath.EndsWith("/oauth.html", StringComparison.Ordinal))
            throw new ArgumentException("The callbackUrl must be Lidarr's OAuth callback path.", nameof(query));

        return callback.AbsolutePath[..^"oauth.html".Length] +
               $"plugins/mshihadeh1/ai-import-workspace.html?providerId={Definition.Id}";
    }

    private static string RequiredQuery(IDictionary<string, string> query, string name) =>
        query.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : throw new ArgumentException($"The {name} query value is required.", nameof(query));

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
