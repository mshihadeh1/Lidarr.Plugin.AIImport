using Lidarr.Plugin.AIImport.Configuration;
using Lidarr.Plugin.AIImport.Discovery;
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
    public string JobId { get; set; } = string.Empty;
    public AiImportDiscoverStatus Status { get; set; } = AiImportDiscoverStatus.Pending;
    public AiImportDiscoverResult? Result { get; set; }

    public override bool SendUpdatesToClient => true;
    public override bool UpdateScheduledTask => false;
    public override bool RequiresDiskAccess => false;
    public override bool IsExclusive => false;
    public override bool IsTypeExclusive => true;
    public override bool IsLongRunning => false;
}

public sealed class AiImportDiscoverCommandHandler : IExecute<AiImportDiscoverCommand>
{
    private readonly IAiImportDiscoverWorkflow _workflow;
    private readonly IAiImportSettingsResolver _settingsResolver;
    private readonly DiscoveryJobStore? _jobs;

    public AiImportDiscoverCommandHandler(
        IAiImportDiscoverWorkflow workflow,
        IAiImportSettingsResolver settingsResolver)
    {
        _workflow = workflow;
        _settingsResolver = settingsResolver;
    }

    public AiImportDiscoverCommandHandler(
        IAiImportDiscoverWorkflow workflow,
        IAiImportSettingsResolver settingsResolver,
        DiscoveryJobStore jobs)
        : this(workflow, settingsResolver)
    {
        _jobs = jobs;
    }

    public void Execute(AiImportDiscoverCommand message)
    {
        message.Status = AiImportDiscoverStatus.Running;
        message.Result = null;
        if (!string.IsNullOrWhiteSpace(message.JobId)) _jobs?.MarkRunning(message.JobId);
        try
        {
            var settings = _settingsResolver.ResolveEnabled(message.ProviderDefinitionId);
            message.Result = _workflow.Discover(settings);
            message.Status = AiImportDiscoverStatus.Succeeded;
            if (!string.IsNullOrWhiteSpace(message.JobId)) _jobs?.Complete(message.JobId, message.Result);
        }
        catch (Exception exception)
        {
            message.Status = AiImportDiscoverStatus.Failed;
            if (!string.IsNullOrWhiteSpace(message.JobId))
                _jobs?.MarkIndeterminate(message.JobId,
                    $"Server-side discovery stopped without a verified completion: {exception.Message}");
            throw;
        }
    }
}

public interface IDiscoveryCommandSubmitter
{
    int Submit(AiImportDiscoverCommand command);
    CommandModel? Get(int commandId);
    CommandModel? Find(string jobId);
}

public sealed class DiscoveryCommandSubmitter(IManageCommandQueue commands) : IDiscoveryCommandSubmitter
{
    public int Submit(AiImportDiscoverCommand command) =>
        commands.Push(command, CommandPriority.Normal, CommandTrigger.Manual).Id;

    public CommandModel? Get(int commandId)
    {
        try
        {
            return commands.Get(commandId);
        }
        catch
        {
            return null;
        }
    }

    public CommandModel? Find(string jobId) => commands.All().SingleOrDefault(model =>
        model.Body is AiImportDiscoverCommand command &&
        string.Equals(command.JobId, jobId, StringComparison.Ordinal));
}
