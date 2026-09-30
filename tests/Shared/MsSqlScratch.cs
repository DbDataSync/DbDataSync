using Microsoft.Data.SqlClient;

namespace DbDataSync.TestSupport;

/// <summary>
/// Drops a test's scratch SQL Server database. Linked into every test project that makes one (see each csproj's
/// <c>Shared\MsSqlScratch.cs</c> item), so there is one teardown, not a copy per class.
/// <para>
/// **Why not <c>SET SINGLE_USER WITH ROLLBACK IMMEDIATE</c> and then <c>DROP DATABASE</c>**, the two separate
/// commands every fixture used to send: that kicks everyone off and frees exactly one slot, and anything still
/// using the database can reconnect into it before the <c>DROP</c> arrives. That is "Cannot drop database …
/// because it is currently in use". The usual holder is a replication pass the test's own API host started (a
/// spawned TaskRunner, with its own pool in its own process), or a pooled connection of the host's. CI run
/// <c>36655178044</c> hit this in <c>DescriptorDriverTests</c>.
/// </para>
/// <para>
/// So the kick and the drop go in **one batch**, with no client round trip for anything to land in. A failure
/// repeats the **whole** batch, re-kicking whoever took the slot, rather than retrying only the <c>DROP</c>,
/// which cannot evict a session already holding it. This process's pools are cleared first and between
/// attempts. That covers the host's connections; a TaskRunner's are in another process, which is what the
/// re-kick is for.
/// </para>
/// </summary>
internal static class MsSqlScratch
{
    private const int Attempts = 10;
    private static readonly TimeSpan Pause = TimeSpan.FromMilliseconds(500);

    /// <param name="serverConnection">An open connection to the server, not to <paramref name="database"/>.</param>
    public static async Task DropDatabaseAsync(SqlConnection serverConnection, string database)
    {
        SqlConnection.ClearAllPools();
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await using var command = serverConnection.CreateCommand();
                command.CommandText = Batch(database);
                await command.ExecuteNonQueryAsync();
                return;
            }
            catch (SqlException ex) when (attempt < Attempts && IsContention(ex))
            {
                SqlConnection.ClearAllPools();
                await Task.Delay(Pause);
            }
        }
    }

    /// <summary>The same, for a synchronous teardown (<c>IDisposable</c>), given a server connection string.</summary>
    public static void DropDatabase(string serverConnectionString, string database)
    {
        SqlConnection.ClearAllPools();
        using var connection = new SqlConnection(serverConnectionString);
        connection.Open();
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                using var command = connection.CreateCommand();
                command.CommandText = Batch(database);
                command.ExecuteNonQuery();
                return;
            }
            catch (SqlException ex) when (attempt < Attempts && IsContention(ex))
            {
                SqlConnection.ClearAllPools();
                Thread.Sleep(Pause);
            }
        }
    }

    /// <summary>Idempotent: a database already gone is not an error, so a teardown that ran twice, or after a
    /// setup that failed half way, stays quiet.</summary>
    private static string Batch(string database)
    {
        var quoted = "[" + database.Replace("]", "]]") + "]";
        var literal = "N'" + database.Replace("'", "''") + "'";
        return $"""
            IF DB_ID({literal}) IS NOT NULL
            BEGIN
                ALTER DATABASE {quoted} SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
                DROP DATABASE {quoted};
            END
            """;
    }

    /// <summary>3702: database in use. 5061: ALTER DATABASE could not take its lock. 1205: deadlock victim, which
    /// server-scoped operations running side by side produce.</summary>
    private static bool IsContention(SqlException ex) => ex.Number is 3702 or 5061 or 1205;
}
