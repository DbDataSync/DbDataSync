using Npgsql;

namespace DbDataSync.Drivers.Jdbc.Tests;

/// <summary>
/// The same pattern as <c>DbDataSync.Drivers.Postgres.Tests.PostgresTestDatabase</c> — a uniquely-named
/// database on the real Postgres container from <c>docker-compose.yml</c>, for the lifetime of one test
/// class. Duplicated rather than shared across test projects (this repo has no precedent for a test
/// project referencing another one's fixtures) — small enough that duplication costs less than the
/// dependency would.
/// </summary>
public sealed class JdbcTestDatabase : IAsyncLifetime
{
    public static string ServerConnectionString { get; } =
        Environment.GetEnvironmentVariable("DBDATASYNC_TEST_POSTGRES_SERVER")
        ?? "Host=localhost;Port=15432;Username=dbdatasync;Password=DbDataSync_Test_Pw1;Database=postgres";

    public static string JdbcUrl { get; } =
        Environment.GetEnvironmentVariable("DBDATASYNC_TEST_POSTGRES_JDBC_URL")
        ?? "jdbc:postgresql://localhost:15432/";

    public string DatabaseName { get; } = $"dbdatasync_jdbc_test_{Guid.NewGuid():N}";

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
        await ExecuteAsync(connection, $"DROP DATABASE IF EXISTS \"{DatabaseName}\" WITH (FORCE);");
    }

    public NpgsqlConnection OpenNpgsqlConnection()
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
