using Npgsql;

namespace DbDataSync.Drivers.Generic.Tests;

/// <summary>
/// Creates a uniquely-named database on the same PostgreSQL container
/// <c>DbDataSync.Drivers.Postgres.Tests.PostgresTestDatabase</c> uses, and drops it afterwards. Kept
/// separate rather than shared: this project has no reference to
/// <c>DbDataSync.Drivers.Postgres</c> — the whole point of <see cref="GenericDriver"/> is standing up
/// against an engine through nothing but its ADO.NET provider and a <c>DbDataSync.Core.Sql</c> dialect.
/// </summary>
public sealed class GenericDriverTestDatabase : IAsyncLifetime
{
    public static string Host { get; } = "localhost";
    public static int Port { get; } = 15432;
    public static string User { get; } = "dbdatasync";
    public static string Password { get; } = "DbDataSync_Test_Pw1";

    private static string ServerConnectionString =>
        Environment.GetEnvironmentVariable("DBDATASYNC_TEST_POSTGRES_SERVER")
        ?? $"Host={Host};Port={Port};Username={User};Password={Password};Database=postgres";

    public string DatabaseName { get; } = $"dbdatasync_generic_test_{Guid.NewGuid():N}";

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
