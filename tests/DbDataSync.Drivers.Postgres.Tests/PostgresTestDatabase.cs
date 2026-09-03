using Npgsql;
using Xunit;

namespace DbDataSync.Drivers.Postgres.Tests;

/// <summary>
/// Creates a uniquely-named database on a real PostgreSQL instance for the lifetime of one test class
/// and drops it afterwards. Points at the local Docker container from docker-compose.yml by default;
/// override via DBDATASYNC_TEST_POSTGRES_SERVER.
/// </summary>
public sealed class PostgresTestDatabase : IAsyncLifetime
{
    public static string ServerConnectionString { get; } =
        Environment.GetEnvironmentVariable("DBDATASYNC_TEST_POSTGRES_SERVER")
        ?? "Host=localhost;Port=15432;Username=dbdatasync;Password=DbDataSync_Test_Pw1;Database=postgres";

    // Lower-case: Postgres folds unquoted identifiers down, and a mixed-case database name would then
    // only be reachable quoted — a needless trap in every connection string that names it.
    public string DatabaseName { get; } = $"dbdatasync_test_{Guid.NewGuid():N}";

    public async Task InitializeAsync()
    {
        await using var connection = new NpgsqlConnection(ServerConnectionString);
        await connection.OpenAsync();
        await ExecuteAsync(connection, $"CREATE DATABASE \"{DatabaseName}\";");
    }

    public async Task DisposeAsync()
    {
        await using var connection = new NpgsqlConnection(ServerConnectionString);
        await connection.OpenAsync();
        // WITH (FORCE) drops the database even if a pooled connection is still parked on it, which is
        // otherwise the normal outcome of a test class that opened one.
        await ExecuteAsync(connection, $"DROP DATABASE IF EXISTS \"{DatabaseName}\" WITH (FORCE);");
    }

    public NpgsqlConnection OpenConnection()
    {
        var builder = new NpgsqlConnectionStringBuilder(ServerConnectionString) { Database = DatabaseName };
        var connection = new NpgsqlConnection(builder.ConnectionString);
        connection.Open();
        return connection;
    }

    private static async Task ExecuteAsync(NpgsqlConnection connection, string sql)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }
}
