using Microsoft.Data.Sqlite;

namespace DataSync.State;

/// <summary>
/// Defense-in-depth on top of <see cref="StateDatabase"/>'s busy_timeout: retries a write a handful
/// of times with backoff if SQLite still reports the database as busy/locked after the timeout
/// expires under heavy contention. Per architecture/detailed-design.md §3.7.
/// </summary>
internal static class SqliteRetry
{
    private const int MaxAttempts = 5;

    public static T Execute<T>(Func<T> action)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return action();
            }
            catch (SqliteException ex) when (IsBusy(ex) && attempt < MaxAttempts)
            {
                Thread.Sleep(TimeSpan.FromMilliseconds(50 * attempt));
            }
        }
    }

    public static void Execute(Action action) => Execute<object?>(() =>
    {
        action();
        return null;
    });

    // SQLITE_BUSY = 5, SQLITE_LOCKED = 6.
    private static bool IsBusy(SqliteException ex) => ex.SqliteErrorCode is 5 or 6;
}
