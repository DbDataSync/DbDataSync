using Microsoft.Data.Sqlite;

namespace DataSync.State;

/// <summary>
/// The central SQLite state database (architecture/detailed-design.md §3.7), shared by DataSync.Api
/// and every DataSync.TaskRunner process. Every connection opened through this class has a
/// busy_timeout set, so concurrent readers/writers across processes don't immediately surface
/// SQLITE_BUSY — combined with the retry wrapper in <see cref="SqliteRetry"/>, that's the
/// concurrency mitigation for choosing one central database over per-task files.
/// </summary>
public sealed class StateDatabase
{
    private const int BusyTimeoutMilliseconds = 5000;

    private readonly string _connectionString;

    public StateDatabase(string sqliteFilePath)
    {
        var dir = Path.GetDirectoryName(sqliteFilePath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        // Pooling=False: Microsoft.Data.Sqlite pools native sqlite3 handles by connection string by
        // default. Combined with this file being written by *other processes* (DataSync.TaskRunner),
        // a pooled handle reused across separate OpenConnection() calls risks observing a stale
        // snapshot from whenever it was first opened instead of picking up commits made by other
        // processes since — each caller here already treats a connection as fully short-lived (open,
        // one query, dispose), so there's no pooling benefit worth that risk.
        _connectionString = new SqliteConnectionStringBuilder { DataSource = sqliteFilePath, Pooling = false }.ToString();

        using var connection = OpenRawConnection();
        EnsureSchema(connection);
    }

    /// <summary>
    /// Opens a connection and re-verifies the schema exists on *every* call, not just once at
    /// construction — cheap (a single PRAGMA read in the common case) and makes this self-healing
    /// against a real, still not fully root-caused problem observed in this project's sandboxed dev
    /// environment: a long-lived process (DataSync.Api) that spawns child processes
    /// (DataSync.TaskRunner) writing to this same file could, after the child had been running for
    /// tens of seconds, start seeing an apparently-schema-less view of the database on a fresh
    /// connection ("no such table"), despite migration having already run successfully at startup and
    /// other processes' writes to the same path being independently verifiable on disk. Disabling
    /// pooling and WAL mode (see OpenConnection's own note) did not fully resolve it either. Given
    /// the underlying cause sits below this application (most likely something about how this
    /// sandbox's process/filesystem isolation interacts with a Node-launched process tree spawning
    /// further dotnet child processes — see architecture/implementation/done/phase-006-spa.md), re-checking
    /// and re-applying the schema on every open is a pragmatic, low-cost way to make correctness not
    /// depend on fully understanding that cause.
    /// </summary>
    public SqliteConnection OpenConnection()
    {
        var connection = OpenRawConnection();
        EnsureSchema(connection);
        return connection;
    }

    private SqliteConnection OpenRawConnection()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();

        // Deliberately not WAL mode: WAL relies on a shared-memory (-shm) file to coordinate
        // multiple processes reading/writing the same database, which needs working mmap + POSIX
        // file-locking semantics between those processes. In this project's sandboxed dev
        // environment that coordination was observed to be unreliable across the API process and its
        // spawned DataSync.TaskRunner child processes. The default rollback-journal mode uses plain
        // file locking instead — slower under contention, but that's exactly what busy_timeout +
        // SqliteRetry already exist to absorb, and it doesn't depend on shared-memory coordination
        // working correctly on every deployment filesystem.
        using var pragma = connection.CreateCommand();
        pragma.CommandText = $"PRAGMA busy_timeout = {BusyTimeoutMilliseconds};";
        pragma.ExecuteNonQuery();

        return connection;
    }

    private static void EnsureSchema(SqliteConnection connection)
    {
        var currentVersion = GetUserVersion(connection);

        for (var i = currentVersion; i < Migrations.Scripts.Length; i++)
        {
            using var transaction = connection.BeginTransaction();
            using (var cmd = connection.CreateCommand())
            {
                cmd.Transaction = transaction;
                cmd.CommandText = Migrations.Scripts[i];
                cmd.ExecuteNonQuery();
            }

            transaction.Commit();
            SetUserVersion(connection, i + 1);
        }
    }

    private static int GetUserVersion(SqliteConnection connection)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "PRAGMA user_version;";
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    private static void SetUserVersion(SqliteConnection connection, int version)
    {
        // PRAGMA does not accept bound parameters for its value; `version` is internally controlled
        // (a migration index), never external input.
        using var cmd = connection.CreateCommand();
        cmd.CommandText = $"PRAGMA user_version = {version};";
        cmd.ExecuteNonQuery();
    }
}
