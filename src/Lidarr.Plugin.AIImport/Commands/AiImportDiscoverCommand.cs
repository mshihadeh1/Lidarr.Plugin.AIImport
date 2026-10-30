using Lidarr.Plugin.AIImport.Configuration;
using Lidarr.Plugin.AIImport.Workflows;
using NzbDrone.Core.Messaging.Commands;

namespace Lidarr.Plugin.AIImport.Commands;

public enum AiImportDiscoverStatus
{
    Pending,
    Running,
    Succeeded,
    Failed
}

// Backend command queued by the control API; discovery remains in the standalone workflow.
public sealed class AiImportDiscoverCommand : Command
{
    public int ProviderDefinitionId { get; set; }
    public AiImportDiscoverStatus Status { get; set; } = AiImportDiscoverStatus.Pending;
    public AiImportDiscoverResult? Result { get; set; }

    public override bool SendUpdatesToClient => true;
    public override bool UpdateScheduledTask => false;
    public override bool RequiresDiskAccess => false;
    public override bool IsExclusive => false;
    public override bool IsLongRunning => false;
}

public sealed class AiImportDiscoverCommandHandler : IExecute<AiImportDiscoverCommand>
{
    private readonly IAiImportDiscoverWorkflow _workflow;
    private readonly IAiImportSettingsResolver _settingsResolver;

    public AiImportDiscoverCommandHandler(
        IAiImportDiscoverWorkflow workflow,
        IAiImportSettingsResolver settingsResolver)
    {
        _workflow = workflow;
        _settingsResolver = settingsResolver;
    }

    public void Execute(AiImportDiscoverCommand message)
    {
        message.Status = AiImportDiscoverStatus.Running;
        message.Result = null;

        var settings = _settingsResolver.ResolveEnabled(message.ProviderDefinitionId);
        var result = _workflow.Discover(settings);

        message.Result = result;
        message.Status = AiImportDiscoverStatus.Succeeded;
    }
}
