using System.Text.Json;
using System.Text.Json.Serialization;
using Lidarr.AIImport.Companion.Approval;
using Lidarr.AIImport.Companion.Authorization;
using Lidarr.AIImport.Companion.Configuration;
using Lidarr.AIImport.Companion.Discovery;
using Lidarr.AIImport.Companion.Execution;
using Lidarr.AIImport.Companion.Filesystem;
using Lidarr.AIImport.Companion.Lidarr;
using Lidarr.AIImport.Companion.Library;
using Lidarr.AIImport.Companion.Llm;
using Lidarr.AIImport.Companion.Matching;
using Lidarr.AIImport.Companion.Security;
using Lidarr.AIImport.Companion.Safety;

var builder = WebApplication.CreateBuilder(args);
var options = new CompanionOptions
{
    LidarrBaseUrl = builder.Configuration["LIDARR_BASE_URL"] ?? string.Empty,
    LidarrApiKey = builder.Configuration["LIDARR_API_KEY"] ?? string.Empty,
    CompanionApiToken = builder.Configuration["COMPANION_API_TOKEN"] ?? string.Empty,
    HardlinkScriptPath = builder.Configuration["HARDLINK_SCRIPT_PATH"] ?? string.Empty,
    NativeHelperPath = builder.Configuration["NATIVE_HELPER_PATH"] ?? "/app/lidarr-hardlink-import",
    ApprovalStorePath = builder.Configuration["APPROVAL_STORE_PATH"] ?? "/data/approvals.json",
    AuthorizationDirectory = builder.Configuration["AIIMPORT_AUTHORIZATION_DIR"] ?? "/data/authorizations",
    AuthorizationDeploymentConfirmed = bool.TryParse(builder.Configuration["AUTHORIZATION_DEPLOYMENT_CONFIRMED"], out var deploymentConfirmed) && deploymentConfirmed,
    ApprovalLifetimeMinutes = int.TryParse(builder.Configuration["APPROVAL_LIFETIME_MINUTES"], out var approvalLifetime) ? approvalLifetime : 15,
    LlmBaseUrl = builder.Configuration["LLM_BASE_URL"] ?? string.Empty,
    LlmApiKey = builder.Configuration["LLM_API_KEY"] ?? string.Empty,
    LlmModel = builder.Configuration["LLM_MODEL"] ?? string.Empty,
    LlmTemperature = decimal.TryParse(builder.Configuration["LLM_TEMPERATURE"], out var temperature) ? temperature : 0.1m,
    LlmTimeoutSeconds = int.TryParse(builder.Configuration["LLM_TIMEOUT_SECONDS"], out var timeoutSeconds) ? timeoutSeconds : 60,
    LlmMaximumOutputTokens = int.TryParse(builder.Configuration["LLM_MAXIMUM_OUTPUT_TOKENS"], out var maximumOutputTokens) ? maximumOutputTokens : 2000
};
options.ValidateDiscovery();
var llmConfigured = !string.IsNullOrWhiteSpace(options.LlmBaseUrl) || !string.IsNullOrWhiteSpace(options.LlmModel);
if (llmConfigured) options.ValidateLlm();

builder.Services.ConfigureHttpJsonOptions(json =>
    json.SerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase)));
builder.Services.AddSingleton(options);
builder.Services.AddSingleton<DeterministicMatcher>();
if (llmConfigured)
{
    builder.Services.AddHttpClient<IOpenAiChatClient, OpenAiChatClient>();
    builder.Services.AddTransient<ILlmMatchService, LlmMatchService>();
}
builder.Services.AddSingleton<ImportSafetyService>();
builder.Services.AddSingleton(new ApprovalStore(options.ApprovalStorePath));
var nativeHelperAttestor = new NativeHelperAttestor(options.NativeHelperPath);
builder.Services.AddSingleton(nativeHelperAttestor);
builder.Services.AddSingleton(new TransferAuthorizationStore(
    options.AuthorizationDirectory, options.NativeHelperPath, attestor: nativeHelperAttestor));
builder.Services.AddSingleton<ITransferAuthorizationStore>(services => services.GetRequiredService<TransferAuthorizationStore>());
builder.Services.AddSingleton<ITransferResultVerifier>(new NativeTransferResultVerifier(
    options.NativeHelperPath,
    options.AuthorizationDirectory,
    TimeSpan.FromSeconds(30),
    nativeHelperAttestor));
builder.Services.AddSingleton<IFileIdentityProvider>(new NativeFileIdentityProvider(
    options.NativeHelperPath, TimeSpan.FromSeconds(10), nativeHelperAttestor));
builder.Services.AddTransient<ApprovalService>();
builder.Services.AddTransient<IApprovedPlanRevalidator>(services => services.GetRequiredService<ApprovalService>());
builder.Services.AddTransient<ImportExecutionService>();
builder.Services.AddTransient<ImportReconciliationService>();
builder.Services.AddTransient<IImportReconciliationService>(services => services.GetRequiredService<ImportReconciliationService>());
builder.Services.AddTransient<ImportReconciliationCoordinator>();
builder.Services.AddTransient<MissingAlbumService>();
builder.Services.AddTransient<IMissingAlbumExecutor>(services => services.GetRequiredService<MissingAlbumService>());
builder.Services.AddTransient<MissingAlbumApprovalService>();
builder.Services.AddSingleton<IFailedImportDiscoveryService, FailedImportDiscoveryService>();
builder.Services.AddHttpClient<LidarrClient>(client =>
{
    client.Timeout = TimeSpan.FromSeconds(60);
});
builder.Services.AddTransient<ILidarrClient>(services => services.GetRequiredService<LidarrClient>());
builder.Services.AddTransient<ILidarrLibraryClient>(services => services.GetRequiredService<LidarrClient>());
builder.Services.AddTransient<ILidarrExecutionClient>(services => services.GetRequiredService<LidarrClient>());
builder.Services.AddTransient<ILidarrReconciliationClient>(services => services.GetRequiredService<LidarrClient>());
builder.Services.AddTransient<IImportSafetySnapshotProvider, ImportSafetySnapshotProvider>();

var app = builder.Build();
await app.Services.GetRequiredService<ApprovalStore>().RecoverInterruptedExecutionsAsync(CancellationToken.None);
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
    mode = "approval-required",
    compatibleLidarrVersion = FailedImportDiscoveryService.CompatibleLidarrVersion
}));
app.MapGet("/api/v1/failed-imports", async (
    IFailedImportDiscoveryService discovery,
    CancellationToken cancellationToken) =>
    Results.Ok(await discovery.DiscoverAsync(cancellationToken)));
app.MapGet("/api/v1/import-safety", async (
    ILidarrClient lidarr,
    ImportSafetyService safety,
    CancellationToken cancellationToken) =>
    Results.Ok(safety.Assess(
        await lidarr.GetSystemStatusAsync(cancellationToken),
        await lidarr.GetImportSafetyConfigurationAsync(cancellationToken))));
app.MapPost("/api/v1/analyses/{analysisId}/missing-album", async (
    string analysisId,
    MissingAlbumApprovalRequest request,
    MissingAlbumApprovalService approvals,
    CancellationToken cancellationToken) =>
{
    try
    {
        return Results.Ok(await approvals.ApproveAsync(
            analysisId,
            request.RootFolderId,
            cancellationToken));
    }
    catch (StaleAnalysisException exception)
    {
        return Results.Conflict(new { error = exception.Message });
    }
    catch (Exception exception) when (exception is ArgumentException or InvalidDataException or InvalidOperationException)
    {
        return Results.BadRequest(new { error = exception.Message });
    }
});
app.MapPost("/api/v1/analyses/{analysisId}/approve-import", async (
    string analysisId,
    ApprovalService approvals,
    CancellationToken cancellationToken) =>
{
    try
    {
        return Results.Ok(await approvals.ApproveAsync(
            analysisId,
            TimeSpan.FromMinutes(options.ApprovalLifetimeMinutes),
            cancellationToken));
    }
    catch (StaleApprovalException exception)
    {
        return Results.Conflict(new { error = exception.Message });
    }
    catch (Exception exception) when (exception is ArgumentException or InvalidDataException or InvalidOperationException)
    {
        return Results.BadRequest(new { error = exception.Message });
    }
});
app.MapPost("/api/v1/approvals/{approvalId}/status", async (
    string approvalId,
    ApprovalCredentialRequest request,
    ApprovalStore approvals,
    CancellationToken cancellationToken) =>
{
    try
    {
        var record = await approvals.AuthenticateForStatusAsync(approvalId, request.Token, cancellationToken);
        return Results.Ok(new
        {
            record.ApprovalId,
            record.AnalysisId,
            record.State,
            record.CreatedAt,
            record.ExpiresAt,
            record.ExecutionStartedAt,
            record.CompletedAt,
            record.Outcome,
            record.CommandId,
            record.CommandStatus,
            AuthorizationCount = record.AuthorizationBindings?.Count ?? 0
        });
    }
    catch (ApprovalAuthenticationException)
    {
        return Results.Unauthorized();
    }
});
app.MapPost("/api/v1/approvals/{approvalId}/execute", async (
    string approvalId,
    ApprovalCredentialRequest request,
    ImportExecutionService execution,
    CancellationToken cancellationToken) =>
{
    try
    {
        var started = await execution.ExecuteAsync(approvalId, request.Token, cancellationToken);
        return Results.Accepted(
            $"/api/v1/approvals/{Uri.EscapeDataString(approvalId)}/reconcile",
            started);
    }
    catch (Exception exception) when (exception is ApprovalReplayException or ApprovalExpiredException or StaleApprovalException)
    {
        return Results.Conflict(new { error = exception.Message });
    }
    catch (ApprovalAuthenticationException)
    {
        return Results.Unauthorized();
    }
    catch (ImportOutcomeIndeterminateException exception)
    {
        return Results.Json(new { error = exception.Message }, statusCode: StatusCodes.Status502BadGateway);
    }
    catch (Exception exception) when (exception is ArgumentException or InvalidDataException or InvalidOperationException)
    {
        return Results.BadRequest(new { error = exception.Message });
    }
});
app.MapPost("/api/v1/approvals/{approvalId}/reconcile", async (
    string approvalId,
    ApprovalCredentialRequest request,
    ImportReconciliationCoordinator reconciliation,
    CancellationToken cancellationToken) =>
{
    try
    {
        var result = await reconciliation.ReconcileAsync(approvalId, request.Token, cancellationToken);
        return result.Status == ImportReconciliationStatus.Pending
            ? Results.Accepted(value: result)
            : Results.Ok(result);
    }
    catch (ApprovalAuthenticationException)
    {
        return Results.Unauthorized();
    }
    catch (ApprovalReplayException exception)
    {
        return Results.Conflict(new { error = exception.Message });
    }
    catch (ImportReconciliationException exception)
    {
        return Results.Json(new { error = exception.Message }, statusCode: StatusCodes.Status409Conflict);
    }
    catch (Exception exception) when (exception is ArgumentException or InvalidDataException or InvalidOperationException)
    {
        return Results.BadRequest(new { error = exception.Message });
    }
});
app.Run();

public sealed record MissingAlbumApprovalRequest(int RootFolderId);
public sealed record ApprovalCredentialRequest(string Token);
public partial class Program;
