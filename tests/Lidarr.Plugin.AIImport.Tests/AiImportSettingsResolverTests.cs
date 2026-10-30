using System.Reflection;
using Lidarr.Plugin.AIImport.Configuration;
using NzbDrone.Core.Notifications;

namespace Lidarr.Plugin.AIImport.Tests;

[TestFixture]
public sealed class AiImportSettingsResolverTests
{
    [Test]
    public void Resolves_a_saved_action_only_provider_without_notification_triggers()
    {
        var settings = new AiImportSettings
        {
            ApiBaseUrl = "https://saved.example/v1",
            Model = "saved-model"
        };
        var definition = new NotificationDefinition
        {
            Id = 7,
            Implementation = nameof(AiImportConnector),
            Settings = settings
        };
        var repository = DispatchProxy.Create<INotificationRepository, RepositoryProxy>();
        ((RepositoryProxy)(object)repository).Definitions = [definition];

        var resolved = new AiImportSettingsResolver(repository).ResolveEnabled(7);

        Assert.That(resolved, Is.SameAs(settings));
        Assert.That(definition.Enable, Is.False,
            "Action-only providers have no notification trigger and therefore cannot set ProviderDefinition.Enable.");
    }

    private class RepositoryProxy : DispatchProxy
    {
        public IReadOnlyList<NotificationDefinition> Definitions { get; set; } = [];

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name == "All") return Definitions.ToList();
            throw new NotSupportedException(targetMethod?.Name);
        }
    }
}
