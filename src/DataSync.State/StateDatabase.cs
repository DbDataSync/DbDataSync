using Microsoft.Data.Sqlite;

namespace DataSync.State;

/// <summary>
/// The central SQLite state database (architecture/detailed-design.md §3.7), shared by DataSync.Api
/// and every DataSync.TaskRunner process. Every connection opened through this class has WAL mode
/// and a busy_timeout set, so concurrent readers/writers across processes don't immediately surface
/// SQLITE_BUSY — combined with the retry wrapper in <see cref="SqliteRetry"/>, that's the whole
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

        _connectionString = new SqliteConnectionStringBuilder { DataSource = sqliteFilePath }.ToString();

        Migrate();
    }

    public SqliteConnection OpenConnection()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();

        using var pragma = connection.CreateCommand();
        pragma.CommandText = $"PRAGMA journal_mode = WAL; PRAGMA busy_timeout = {BusyTimeoutMilliseconds};";
        pragma.ExecuteNonQuery();

        return connection;
    }

    private void Migrate()
    {
        using var connection = OpenConnection();
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
