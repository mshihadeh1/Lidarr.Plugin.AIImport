using Lidarr.Plugin.AIImport.Configuration;
using Lidarr.Plugin.AIImport.Llm;
using NzbDrone.Core.Notifications;

namespace Lidarr.Plugin.AIImport.Tests;

[TestFixture]
public sealed class AiImportConnectorTests
{
    [Test]
    public void Test_redacts_the_configured_api_key_from_errors()
    {
        const string apiKey = "super-secret-key";
        var connector = new AiImportConnector(new ThrowingClient($"Endpoint rejected {apiKey}"))
        {
            Definition = new NotificationDefinition
            {
                Settings = new AiImportSettings
                {
                    ApiBaseUrl = "https://example.test/v1",
                    ApiKey = apiKey,
                    Model = "model"
                }
            }
        };

        var result = connector.Test();
        var error = result.Errors.Single().ErrorMessage;

        Assert.That(error, Does.Not.Contain(apiKey));
        Assert.That(error, Does.Contain("[REDACTED]"));
    }

    private sealed class ThrowingClient(string message) : IOpenAiChatClient
    {
        public Task<LlmTestResult> TestConnectionAsync(AiImportSettings settings, CancellationToken cancellationToken) =>
            Task.FromException<LlmTestResult>(new InvalidOperationException(message));

        public Task<string> CompleteJsonAsync(AiImportSettings settings, string systemPrompt, string userPrompt, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}