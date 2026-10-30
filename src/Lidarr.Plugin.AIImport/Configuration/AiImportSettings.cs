using FluentValidation;
using NzbDrone.Core.Annotations;
using NzbDrone.Core.ThingiProvider;
using NzbDrone.Core.Validation;

namespace Lidarr.Plugin.AIImport.Configuration;

public sealed class AiImportSettings : IProviderConfig
{
    private static readonly AiImportSettingsValidator Validator = new();

    [FieldDefinition(0, Label = "API Base URL", Type = FieldType.Url, HelpText = "OpenAI-compatible API base URL, for example http://localhost:8080/v1")]
    public string ApiBaseUrl { get; set; } = "http://localhost:8080/v1";

    [FieldDefinition(1, Label = "API Key", Type = FieldType.Password, Privacy = PrivacyLevel.ApiKey, HelpText = "Optional for local servers")]
    public string ApiKey { get; set; } = string.Empty;

    [FieldDefinition(2, Label = "Model", HelpText = "Provider model identifier")]
    public string Model { get; set; } = string.Empty;

    [FieldDefinition(3, Label = "Temperature", Type = FieldType.Number, HelpText = "Sampling temperature from 0 to 2")]
    public decimal Temperature { get; set; } = 0.1m;

    [FieldDefinition(4, Label = "Request Timeout", Type = FieldType.Number, Unit = "seconds")]
    public int RequestTimeoutSeconds { get; set; } = 60;

    [FieldDefinition(5, Label = "Maximum Output Tokens", Type = FieldType.Number)]
    public int MaximumOutputTokens { get; set; } = 2000;

    [FieldDefinition(6, Label = "Custom System Prompt", HelpText = "Optional instructions appended to the built-in safety prompt. IDs are always verified independently.")]
    public string CustomSystemPrompt { get; set; } = string.Empty;

    [FieldDefinition(7, Label = "Disable Model Thinking", Type = FieldType.Checkbox, Advanced = true, HelpText = "Send enable_thinking=false to compatible local endpoints such as llama.cpp serving Qwen reasoning models")]
    public bool DisableThinking { get; set; }

    [FieldDefinition(99, Label = "Open AI Import Workspace", Type = FieldType.OAuth, HelpText = "Open the in-Lidarr discovery, review, approval, execution, status, and reconciliation workspace")]
    public string Workspace { get; set; } = string.Empty;

    public NzbDroneValidationResult Validate() => new(Validator.Validate(this));
}

public sealed class AiImportSettingsValidator : AbstractValidator<AiImportSettings>
{
    public AiImportSettingsValidator()
    {
        RuleFor(x => x.ApiBaseUrl)
            .Must(value => Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
                           (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps) &&
                           string.IsNullOrEmpty(uri.UserInfo) &&
                           string.IsNullOrEmpty(uri.Query) &&
                           string.IsNullOrEmpty(uri.Fragment))
            .WithMessage("API Base URL must be an absolute HTTP or HTTPS URL without credentials, query, or fragment");
        RuleFor(x => x.Model).NotEmpty();
        RuleFor(x => x.Temperature).InclusiveBetween(0m, 2m);
        RuleFor(x => x.RequestTimeoutSeconds).InclusiveBetween(1, 600);
        RuleFor(x => x.MaximumOutputTokens).InclusiveBetween(1, 1000000);
    }
}
