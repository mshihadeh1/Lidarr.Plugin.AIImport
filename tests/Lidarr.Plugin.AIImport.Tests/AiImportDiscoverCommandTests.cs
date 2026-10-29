using Lidarr.Plugin.AIImport.Commands;
using Lidarr.Plugin.AIImport.Configuration;
using Lidarr.Plugin.AIImport.Workflows;
using NzbDrone.Core.Messaging.Commands;

namespace Lidarr.Plugin.AIImport.Tests;

[TestFixture]
public sealed class AiImportDiscoverCommandTests
{
    [Test]
    public void Command_is_safe_read_only_and_publishes_updates()
    {
        var command = new AiImportDiscoverCommand();

        Assert.Multiple(() =>
        {
            Assert.That(command.SendUpdatesToClient, Is.True);
            Assert.That(command.UpdateScheduledTask, Is.False);
            Assert.That(command.RequiresDiskAccess, Is.False);
            Assert.That(command.IsExclusive, Is.False);
            Assert.That(command.IsLongRunning, Is.False);
        });
    }

    [Test]
    public void Handler_resolves_saved_provider_by_exact_id_and_delegates_to_workflow()
    {
        var settings = new AiImportSettings { ApiBaseUrl = "https://example.test/v1", Model = "model" };
        var result = new AiImportDiscoverResult([]);
        var workflow = new StubDiscoverWorkflow(result);
        var resolver = new StubResolver(settings);
        var command = new AiImportDiscoverCommand { ProviderDefinitionId = 27 };

        new AiImportDiscoverCommandHandler(workflow, resolver).Execute(command);

        Assert.Multiple(() =>
        {
            Assert.That(resolver.RequestedId, Is.EqualTo(27));
            Assert.That(workflow.Settings, Is.SameAs(settings));
            Assert.That(command.Status, Is.EqualTo(AiImportDiscoverStatus.Succeeded));
            Assert.That(command.Result, Is.SameAs(result));
        });
    }

    [Test]
    public void Command_and_handler_are_discoverable_by_lidarr_command_infrastructure()
    {
        Assert.Multiple(() =>
        {
            Assert.That(typeof(AiImportDiscoverCommand).IsSubclassOf(typeof(Command)), Is.True);
            Assert.That(typeof(IExecute<AiImportDiscoverCommand>).IsAssignableFrom(typeof(AiImportDiscoverCommandHandler)), Is.True);
        });
    }

    private sealed class StubDiscoverWorkflow(AiImportDiscoverResult result) : IAiImportDiscoverWorkflow
    {
        public AiImportSettings? Settings { get; private set; }
        public AiImportDiscoverResult Discover(AiImportSettings settings)
        {
            Settings = settings;
            return result;
        }
    }

    private sealed class StubResolver(AiImportSettings settings) : IAiImportSettingsResolver
    {
        public int RequestedId { get; private set; }
        public AiImportSettings ResolveEnabled(int requestedDefinitionId)
        {
            RequestedId = requestedDefinitionId;
            return settings;
        }
    }
}
