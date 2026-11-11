using NLog;

namespace Lidarr.Plugin.AIImport.Diagnostics;

internal static class AiImportWorkflowDiagnostics
{
    private static readonly Logger Logger = LogManager.GetLogger("Lidarr.Plugin.AIImport.Workflow");

    public static void Info(string operation, string stage, string? correlationId = null, string? detail = null)
    {
        Logger.Info(Format(operation, stage, correlationId, detail));
    }

    public static void Warn(string operation, string stage, Exception exception, string? correlationId = null)
    {
        Logger.Warn(exception, Format(operation, stage, correlationId, exception.GetType().Name));
    }

    public static void Error(string operation, string stage, Exception exception, string? correlationId = null)
    {
        Logger.Error(exception, Format(operation, stage, correlationId, exception.GetType().Name));
    }

    private static string Format(string operation, string stage, string? correlationId, string? detail)
    {
        var message = $"AIImport operation={Safe(operation)} stage={Safe(stage)}";
        if (!string.IsNullOrWhiteSpace(correlationId)) message += $" correlation={Safe(correlationId)}";
        if (!string.IsNullOrWhiteSpace(detail)) message += $" detail={Safe(detail)}";
        return message;
    }

    private static string Safe(string value) =>
        value.Replace(' ', '_').Replace('\r', '_').Replace('\n', '_');
}
