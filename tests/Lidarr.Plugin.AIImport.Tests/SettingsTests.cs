using Lidarr.Plugin.AIImport.Configuration;

namespace Lidarr.Plugin.AIImport.Tests;

[TestFixture]
public sealed class SettingsTests
{
    [Test]
    public void Allows_local_endpoint_without_api_key()
    {
        var settings = new AiImportSettings
        {
            ApiBaseUrl = "http://localhost:8080/v1",
            ApiKey = string.Empty,
            Model = "local-model"
        };

        Assert.That(settings.Validate().IsValid, Is.True);
    }

    [TestCase("ftp://localhost/v1")]
    [TestCase("not a url")]
    public void Rejects_non_http_endpoint(string url)
    {
        var settings = new AiImportSettings { ApiBaseUrl = url, Model = "model" };
        Assert.That(settings.Validate().IsValid, Is.False);
    }

    [TestCase("https://user:password@example.test/v1")]
    [TestCase("https://example.test/v1?api-version=1")]
    [TestCase("https://example.test/v1#fragment")]
    public void Rejects_endpoint_components_that_cannot_be_safely_appended(string url)
    {
        var settings = new AiImportSettings { ApiBaseUrl = url, Model = "model" };
        Assert.That(settings.Validate().IsValid, Is.False);
    }

    [TestCase(-0.1)]
    [TestCase(2.1)]
    public void Rejects_temperature_outside_openai_range(double value)
    {
        var settings = new AiImportSettings { ApiBaseUrl = "http://localhost:8080/v1", Model = "model", Temperature = (decimal)value };
        Assert.That(settings.Validate().IsValid, Is.False);
    }

    [TestCase("")]
    [TestCase("   ")]
    public void Rejects_empty_model(string model)
    {
        var settings = new AiImportSettings { ApiBaseUrl = "http://localhost:8080/v1", Model = model };
        Assert.That(settings.Validate().IsValid, Is.False);
    }
}
