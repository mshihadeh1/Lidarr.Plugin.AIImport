using NzbDrone.Core.Notifications;

namespace Lidarr.Plugin.AIImport.Configuration;

public interface IAiImportSettingsResolver
{
    AiImportSettings ResolveEnabled(int requestedDefinitionId);
}

public sealed class AiImportSettingsResolver : IAiImportSettingsResolver
{
    private readonly INotificationRepository _repository;

    public AiImportSettingsResolver(INotificationRepository repository)
    {
        _repository = repository;
    }

    public AiImportSettings ResolveEnabled(int requestedDefinitionId)
    {
        if (requestedDefinitionId <= 0)
        {
            throw new InvalidOperationException("A saved AI Import provider is required for discovery.");
        }

        // AiImportConnector is an action-only notification provider. It supports no
        // notification triggers, so ProviderDefinition.Enable is necessarily false.
        // Saved identity, implementation, settings type, and validation are the trust
        // boundary; requiring Enable would make every real RequestAction unusable.
        var matches = _repository.All()
            .Where(x => x.Id == requestedDefinitionId &&
                        string.Equals(x.Implementation, nameof(AiImportConnector), StringComparison.Ordinal) &&
                        x.Settings is AiImportSettings)
            .ToArray();

        if (matches.Length != 1)
        {
            throw new InvalidOperationException("The requested provider ID does not match a saved AI Import provider.");
        }

        var settings = (AiImportSettings)matches[0].Settings;
        var validation = settings.Validate();
        if (!validation.IsValid)
        {
            throw new InvalidOperationException("The enabled saved AI Import provider settings are invalid.");
        }

        return settings;
    }
}
