namespace Lidarr.AIImport.Companion.Configuration;

public sealed class CompanionOptions
{
    public string LidarrBaseUrl { get; set; } = string.Empty;
    public string LidarrApiKey { get; set; } = string.Empty;
    public string CompanionApiToken { get; set; } = string.Empty;
    public string LlmBaseUrl { get; set; } = string.Empty;
    public string LlmApiKey { get; set; } = string.Empty;
    public string LlmModel { get; set; } = string.Empty;
    public decimal LlmTemperature { get; set; } = 0.1m;
    public int LlmTimeoutSeconds { get; set; } = 60;
    public int LlmMaximumOutputTokens { get; set; } = 2000;

    public void Validate()
    {
        ValidateDiscovery();
        ValidateBaseUrl(LlmBaseUrl, nameof(LlmBaseUrl));
        if (string.IsNullOrWhiteSpace(LlmModel))
        {
            throw new InvalidOperationException("LlmModel is required.");
        }
        if (LlmTemperature is < 0m or > 2m || LlmTimeoutSeconds is < 1 or > 600 || LlmMaximumOutputTokens is < 1 or > 1000000)
        {
            throw new InvalidOperationException("LLM numeric settings are outside their supported ranges.");
        }
    }

    public void ValidateDiscovery()
    {
        ValidateBaseUrl(LidarrBaseUrl, nameof(LidarrBaseUrl));
        if (string.IsNullOrWhiteSpace(LidarrApiKey))
        {
            throw new InvalidOperationException("LidarrApiKey is required.");
        }
        if (string.IsNullOrWhiteSpace(CompanionApiToken))
        {
            throw new InvalidOperationException("CompanionApiToken is required.");
        }
    }

    private static void ValidateBaseUrl(string value, string name)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) ||
            !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
        {
            throw new InvalidOperationException($"{name} must be an absolute HTTP(S) URL without credentials, query, or fragment.");
        }
    }
}
