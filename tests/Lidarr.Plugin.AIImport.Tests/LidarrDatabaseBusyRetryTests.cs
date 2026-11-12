using Lidarr.Plugin.AIImport.Lidarr;

namespace Lidarr.Plugin.AIImport.Tests;

[TestFixture]
public sealed class LidarrDatabaseBusyRetryTests
{
    [Test]
    public void Retries_transient_database_lock_with_bounded_backoff()
    {
        var attempts = 0;
        var waits = new List<TimeSpan>();

        var result = LidarrDatabaseBusyRetry.Execute(
            () =>
            {
                attempts++;
                if (attempts < 3)
                    throw new InvalidOperationException("database is locked\ndatabase is locked");
                return "complete";
            },
            waits.Add);

        Assert.That(result, Is.EqualTo("complete"));
        Assert.That(attempts, Is.EqualTo(3));
        Assert.That(waits, Is.EqualTo(new[]
        {
            TimeSpan.FromMilliseconds(100),
            TimeSpan.FromMilliseconds(250)
        }));
    }

    [Test]
    public void Persistent_database_lock_is_rethrown_after_bounded_attempts()
    {
        var attempts = 0;
        var waits = new List<TimeSpan>();

        var exception = Assert.Throws<InvalidOperationException>(() =>
            LidarrDatabaseBusyRetry.Execute<int>(
                () =>
                {
                    attempts++;
                    throw new InvalidOperationException("database is locked");
                },
                waits.Add));

        Assert.That(exception!.Message, Is.EqualTo("database is locked"));
        Assert.That(attempts, Is.EqualTo(6));
        Assert.That(waits, Has.Count.EqualTo(5));
        Assert.That(waits.Aggregate(TimeSpan.Zero, (total, delay) => total + delay),
            Is.EqualTo(TimeSpan.FromMilliseconds(3850)));
    }

    [Test]
    public void Does_not_retry_unrelated_failures()
    {
        var attempts = 0;
        var waits = new List<TimeSpan>();

        Assert.Throws<InvalidDataException>(() =>
            LidarrDatabaseBusyRetry.Execute<int>(
                () =>
                {
                    attempts++;
                    throw new InvalidDataException("invalid manual-import response");
                },
                waits.Add));

        Assert.That(attempts, Is.EqualTo(1));
        Assert.That(waits, Is.Empty);
    }
}
