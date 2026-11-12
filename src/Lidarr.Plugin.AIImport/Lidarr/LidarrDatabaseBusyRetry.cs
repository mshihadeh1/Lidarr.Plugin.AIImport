namespace Lidarr.Plugin.AIImport.Lidarr;

internal static class LidarrDatabaseBusyRetry
{
    private static readonly TimeSpan[] Delays =
    [
        TimeSpan.FromMilliseconds(100),
        TimeSpan.FromMilliseconds(250),
        TimeSpan.FromMilliseconds(500),
        TimeSpan.FromSeconds(1),
        TimeSpan.FromSeconds(2)
    ];

    internal static T Execute<T>(Func<T> action, Action<TimeSpan>? wait = null)
    {
        ArgumentNullException.ThrowIfNull(action);
        wait ??= Thread.Sleep;

        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return action();
            }
            catch (Exception exception) when (IsDatabaseLocked(exception) && attempt < Delays.Length)
            {
                wait(Delays[attempt]);
            }
        }
    }

    internal static bool IsDatabaseLocked(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current.Message.Contains("database is locked", StringComparison.OrdinalIgnoreCase) ||
                current.Message.Contains("database table is locked", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
