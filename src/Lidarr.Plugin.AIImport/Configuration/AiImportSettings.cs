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

    [FieldDefinition(8, Label = "Untracked Download Folders", Type = FieldType.Textbox, HelpText = "Optional. Absolute folders to scan for albums downloaded outside Lidarr (e.g. directly in the qBittorrent download path). Separate multiple folders with a pipe, comma, or newline. The 25 newest album folders are scanned per discovery.")]
    public string ScanRoots { get; set; } = string.Empty;

    [FieldDefinition(9, Label = "YOLO Mode", Type = FieldType.Checkbox, HelpText = "Standing authorization for unattended imports. Missing releases may be resolved through MusicBrainz and added to Lidarr; complete verified mappings are approved, hardlink-imported, and reconciled automatically. Source, ownership, destination, hardlink, and post-import validation still fail closed.")]
    public bool YoloMode { get; set; }

    [FieldDefinition(10, Label = "MusicBrainz Candidate Limit", Type = FieldType.Number, Advanced = true, HelpText = "Maximum verified MusicBrainz releases fetched for a low-confidence album. Each release lookup is rate-limited and cached.")]
    public int MusicBrainzCandidateLimit { get; set; } = 5;

    [FieldDefinition(11, Label = "YOLO Root Folder ID", Type = FieldType.Number, HelpText = "Required when YOLO mode is enabled. The exact accessible Lidarr root-folder ID used for automatic library additions.")]
    public int YoloRootFolderId { get; set; }

    [FieldDefinition(12, Label = "YOLO Quality Profile ID", Type = FieldType.Number, HelpText = "Required when YOLO mode is enabled. The exact Lidarr quality-profile ID used for automatic library additions.")]
    public int YoloQualityProfileId { get; set; }

    [FieldDefinition(13, Label = "YOLO Metadata Profile ID", Type = FieldType.Number, HelpText = "Required when YOLO mode is enabled. The exact Lidarr metadata-profile ID used for automatic library additions.")]
    public int YoloMetadataProfileId { get; set; }

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
        RuleFor(x => x.ScanRoots)
            .Must(value => SplitScanRoots(value).All(x => Path.IsPathFullyQualified(x)))
            .WithMessage("Untracked download folders must be absolute paths");
        RuleFor(x => x.Temperature).InclusiveBetween(0m, 2m);
        RuleFor(x => x.RequestTimeoutSeconds).InclusiveBetween(1, 600);
        RuleFor(x => x.MaximumOutputTokens).InclusiveBetween(1, 1000000);
        RuleFor(x => x.MusicBrainzCandidateLimit).InclusiveBetween(1, 10);
        RuleFor(x => x.YoloRootFolderId).GreaterThan(0).When(x => x.YoloMode);
        RuleFor(x => x.YoloQualityProfileId).GreaterThan(0).When(x => x.YoloMode);
        RuleFor(x => x.YoloMetadataProfileId).GreaterThan(0).When(x => x.YoloMode);
    }

    internal static string[] SplitScanRoots(string value) =>
        (value ?? string.Empty)
            .Split(['\n', ',', '|', ';'], StringSplitOptions.RemoveEmptyEntries)
            .Select(x => x.Trim())
            .Where(x => x.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
}
