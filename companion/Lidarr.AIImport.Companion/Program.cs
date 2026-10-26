using System.Text.Json;
using System.Text.Json.Serialization;
using Lidarr.AIImport.Companion.Configuration;
using Lidarr.AIImport.Companion.Discovery;
using Lidarr.AIImport.Companion.Lidarr;
using Lidarr.AIImport.Companion.Matching;
using Lidarr.AIImport.Companion.Security;

var builder = WebApplication.CreateBuilder(args);
var options = new CompanionOptions
{
    LidarrBaseUrl = builder.Configuration["LIDARR_BASE_URL"] ?? string.Empty,
    LidarrApiKey = builder.Configuration["LIDARR_API_KEY"] ?? string.Empty,
    CompanionApiToken = builder.Configuration["COMPANION_API_TOKEN"] ?? string.Empty
};
options.ValidateDiscovery();

builder.Services.ConfigureHttpJsonOptions(json =>
    json.SerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase)));
builder.Services.AddSingleton(options);
builder.Services.AddSingleton<DeterministicMatcher>();
builder.Services.AddSingleton<IFailedImportDiscoveryService, FailedImportDiscoveryService>();
builder.Services.AddHttpClient<ILidarrClient, LidarrClient>(client =>
{
    client.Timeout = TimeSpan.FromSeconds(60);
});

var app = builder.Build();
app.Use(async (context, next) =>
{
    if (context.Request.Path.StartsWithSegments("/api") &&
        !CompanionAuthentication.IsAuthorized(
            context.Request.Headers[CompanionAuthentication.HeaderName].FirstOrDefault(),
            options.CompanionApiToken))
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        return;
    }
    await next(context);
});
app.MapGet("/health", () => Results.Ok(new
{
    status = "ok",
    mode = "read-only",
    compatibleLidarrVersion = FailedImportDiscoveryService.CompatibleLidarrVersion
}));
app.MapGet("/api/v1/failed-imports", async (
    IFailedImportDiscoveryService discovery,
    CancellationToken cancellationToken) =>
    Results.Ok(await discovery.DiscoverAsync(cancellationToken)));
app.Run();

public partial class Program;
