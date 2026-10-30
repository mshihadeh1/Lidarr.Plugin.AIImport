using System.Text.Json;
using Lidarr.Plugin.AIImport.Approval;
using Lidarr.Plugin.AIImport.Configuration;
using Lidarr.Plugin.AIImport.Discovery;
using Lidarr.Plugin.AIImport.Execution;
using Lidarr.Plugin.AIImport.Llm;
using Lidarr.Plugin.AIImport.Matching;
using Lidarr.Plugin.AIImport.Workflows;
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
            Definition = Definition(new AiImportSettings
            {
                ApiBaseUrl = "https://example.test/v1",
                ApiKey = apiKey,
                Model = "model"
            })
        };

        var result = connector.Test();
        var error = result.Errors.Single().ErrorMessage;

        Assert.That(error, Does.Not.Contain(apiKey));
        Assert.That(error, Does.Contain("[REDACTED]"));
    }

    [Test]
    public void RequestAction_opens_the_same_origin_workspace_without_running_a_workflow()
    {
        var resolver = new StubSettingsResolver(Settings());
        var workflow = new StubWorkflow(Result());
        var connector = Connector(workflow, resolver);

        var response = connector.RequestAction("startOAuth", new Dictionary<string, string>
        {
            ["callbackUrl"] = "https://lidarr.example/lidarr/oauth.html"
        });
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(response));

        Assert.Multiple(() =>
        {
            Assert.That(json.RootElement.GetProperty("oauthUrl").GetString(),
                Is.EqualTo("/lidarr/plugins/mshihadeh1/ai-import-workspace.html?providerId=27"));
            Assert.That(resolver.CallCount, Is.Zero);
            Assert.That(workflow.CallCount, Is.Zero);
        });

        var completion = connector.RequestAction("getOAuthToken", new Dictionary<string, string>());
        Assert.That(JsonSerializer.Serialize(completion), Is.EqualTo("{}"));
    }

    [Test]
    public void RequestAction_rejects_an_untrusted_workspace_callback_path()
    {
        var connector = Connector(new StubWorkflow(Result()), new StubSettingsResolver(Settings()));

        Assert.Throws<ArgumentException>(() => connector.RequestAction("startOAuth", new Dictionary<string, string>
        {
            ["callbackUrl"] = "https://lidarr.example/not-oauth.html"
        }));
    }

    [Test]
    public void RequestAction_rejects_unknown_or_wrong_case_actions_without_calling_dependencies()
    {
        var resolver = new StubSettingsResolver(Settings());
        var workflow = new StubWorkflow(Result());
        var connector = Connector(workflow, resolver);

        Assert.Throws<ArgumentException>(() => connector.RequestAction("delete", new Dictionary<string, string>()));
        Assert.Throws<ArgumentException>(() => connector.RequestAction("Discover", new Dictionary<string, string>()));
        Assert.Multiple(() =>
        {
            Assert.That(resolver.CallCount, Is.Zero);
            Assert.That(workflow.CallCount, Is.Zero);
        });
    }

    [Test]
    public void RequestAction_dispatches_explicit_standalone_state_machine_actions()
    {
        var standalone = new StubStandaloneWorkflow();
        var connector = new AiImportConnector(
            new ThrowingClient("unused"), new StubWorkflow(Result()), standalone, new StubSettingsResolver(Settings()))
        {
            Definition = Definition(Settings())
        };

        connector.RequestAction("approve", new Dictionary<string, string> { ["analysisId"] = "analysis-1" });
        var execute = Credentials();
        execute["executeConfirmation"] = "IMPORT";
        connector.RequestAction("execute", execute);
        connector.RequestAction("reconcile", Credentials());
        connector.RequestAction("status", Credentials());

        Assert.Multiple(() =>
        {
            Assert.That(standalone.AnalysisId, Is.EqualTo("analysis-1"));
            Assert.That(standalone.Calls, Is.EqualTo(new[] { "approve", "execute", "reconcile", "status" }));
            Assert.That(standalone.ApprovalIds, Has.All.EqualTo("approval-1"));
            Assert.That(standalone.ApprovalIds, Has.Count.EqualTo(3));
        });
        Assert.Throws<ArgumentException>(() =>
            connector.RequestAction("execute", new Dictionary<string, string>()));
        Assert.Throws<ArgumentException>(() =>
            connector.RequestAction("execute", Credentials()));
    }

    [Test]
    public void RequestAction_requires_enabled_saved_provider_and_does_not_trust_posted_settings_or_query()
    {
        var saved = Settings();
        saved.Model = "saved-model";
        var posted = Settings();
        posted.ApiKey = "posted-secret";
        posted.Model = "posted-model";
        var resolver = new StubSettingsResolver(saved);
        var workflow = new StubWorkflow(Result());
        var connector = Connector(workflow, resolver);
        connector.Definition = Definition(posted);

        connector.RequestAction("discover", new Dictionary<string, string>
        {
            ["apiKey"] = "query-secret",
            ["model"] = "query-model",
            ["sourcePath"] = "/untrusted/download"
        });

        Assert.Multiple(() =>
        {
            Assert.That(resolver.CallCount, Is.EqualTo(1));
            Assert.That(resolver.RequestedDefinitionId, Is.EqualTo(27));
            Assert.That(workflow.Settings, Is.SameAs(saved));
        });

        var unavailable = Connector(new StubWorkflow(Result()), new ThrowingSettingsResolver());
        Assert.Throws<InvalidOperationException>(() => unavailable.RequestAction("discover", new Dictionary<string, string>()));
    }

    [Test]
    public void RequestAction_rejects_a_fabricated_positive_provider_id()
    {
        var resolver = new StubSettingsResolver(Settings(), savedDefinitionId: 27);
        var connector = Connector(new StubWorkflow(Result()), resolver);
        connector.Definition.Id = 999;

        Assert.Throws<InvalidOperationException>(() =>
            connector.RequestAction("discover", new Dictionary<string, string>()));
    }

    [Test]
    public void RequestAction_returns_sanitized_typed_discovery_and_proposal_output()
    {
        var response = Result();
        var connector = Connector(new StubWorkflow(response), new StubSettingsResolver(Settings()));

        var actual = connector.RequestAction("discover", new Dictionary<string, string>());
        var json = JsonSerializer.Serialize(actual);

        Assert.Multiple(() =>
        {
            Assert.That(actual, Is.SameAs(response));
            Assert.That(response.CandidateCount, Is.EqualTo(1));
            Assert.That(response.Analyses.Single().Proposal.Status, Is.EqualTo(ProposalStatus.Proposed));
            Assert.That(response.Analyses.Single().Proposal.ReleaseId, Is.EqualTo(101));
            Assert.That(json, Does.Not.Contain("/downloads/Artist/Album"));
            Assert.That(json, Does.Not.Contain("saved-api-key"));
            Assert.That(json, Does.Not.Contain("posted-secret"));
            Assert.That(json, Does.Not.Contain("query-secret"));
            Assert.That(json, Does.Not.Contain("SourcePath"));
            Assert.That(json, Does.Not.Contain("ApiKey"));
        });
    }

    private static AiImportConnector Connector(IAiImportDiscoverWorkflow workflow, IAiImportSettingsResolver resolver) =>
        new(new ThrowingClient("unused"), workflow, resolver)
        {
            Definition = Definition(Settings())
        };

    private static NotificationDefinition Definition(AiImportSettings settings) => new()
    {
        Id = 27,
        OnImportFailure = true,
        Implementation = nameof(AiImportConnector),
        Settings = settings
    };

    private static AiImportSettings Settings() => new()
    {
        ApiBaseUrl = "https://saved.example/v1",
        ApiKey = "saved-api-key",
        Model = "model"
    };

    private static Dictionary<string, string> Credentials() => new()
    {
        ["approvalId"] = "approval-1"
    };

    private static AiImportDiscoverResult Result()
    {
        var proposal = new MatchProposal(
            1, ProposalStatus.Proposed, 5, 10, 101, 0.98m, "verified",
            [new TrackProposal("source-1", 1001, 0.99m)]);
        return new AiImportDiscoverResult([
            new AiImportAnalysisDto(
                "analysis-1", "download-1", "Artist - Album", proposal,
                [new SourceFileDto("source-1", "01 - Song.flac", "Artist", "Album", "Song", 1, [1], 180000)],
                [new ReleaseCandidateDto(10, 101, 5, "Artist", "Album", "release-id",
                    [new ReleaseTrackDto(1001, "Song", 1, "1", 180000, false)])],
                false)
        ]);
    }

    private sealed class StubWorkflow(AiImportDiscoverResult result) : IAiImportDiscoverWorkflow
    {
        public int CallCount { get; private set; }
        public AiImportSettings? Settings { get; private set; }

        public AiImportDiscoverResult Discover(AiImportSettings settings)
        {
            CallCount++;
            Settings = settings;
            return result;
        }
    }

    private sealed class StubSettingsResolver(AiImportSettings settings, int savedDefinitionId = 27) : IAiImportSettingsResolver
    {
        public int CallCount { get; private set; }
        public int? RequestedDefinitionId { get; private set; }
        public AiImportSettings ResolveEnabled(int requestedDefinitionId)
        {
            CallCount++;
            RequestedDefinitionId = requestedDefinitionId;
            if (requestedDefinitionId != savedDefinitionId)
            {
                throw new InvalidOperationException("Requested ID does not match saved provider.");
            }
            return settings;
        }
    }

    private sealed class ThrowingSettingsResolver : IAiImportSettingsResolver
    {
        public AiImportSettings ResolveEnabled(int requestedDefinitionId) =>
            throw new InvalidOperationException("An enabled saved AI Import provider is required.");
    }

    private sealed class StubStandaloneWorkflow : IStandaloneAiImportWorkflow
    {
        public List<string> Calls { get; } = [];
        public List<string> ApprovalIds { get; } = [];
        public string? AnalysisId { get; private set; }
        public ApprovalCreatedDto Approve(string analysisId)
        {
            Calls.Add("approve"); AnalysisId = analysisId;
            return new ApprovalCreatedDto("approval-1", DateTimeOffset.UtcNow.AddMinutes(15));
        }
        public ImportExecutionStarted Execute(string approvalId, string confirmation)
        {
            Assert.That(confirmation, Is.EqualTo("IMPORT"));
            Record("execute", approvalId);
            return new ImportExecutionStarted(42, "queued", ["auth"]);
        }
        public ImportReconciliationResult Reconcile(string approvalId)
        {
            Record("reconcile", approvalId);
            return new ImportReconciliationResult(ImportReconciliationStatus.Pending, 0, "pending");
        }
        public ApprovalStatusDto Status(string approvalId)
        {
            Record("status", approvalId);
            return new ApprovalStatusDto(approvalId, "analysis-1", ApprovalState.Executing,
                DateTimeOffset.UtcNow.AddMinutes(10), 42, "queued", null);
        }
        private void Record(string call, string approvalId)
        {
            Calls.Add(call); ApprovalIds.Add(approvalId);
        }
    }

    private sealed class ThrowingClient(string message) : IOpenAiChatClient
    {
        public Task<LlmTestResult> TestConnectionAsync(AiImportSettings settings, CancellationToken cancellationToken) =>
            Task.FromException<LlmTestResult>(new InvalidOperationException(message));

        public Task<string> CompleteJsonAsync(AiImportSettings settings, string systemPrompt, string userPrompt, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
