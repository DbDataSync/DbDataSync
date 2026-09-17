using Oracle.ManagedDataAccess.Client;
using Xunit;

namespace DbDataSync.Drivers.Oracle.Tests;

/// <summary>
/// Points at the local Docker container from docker-compose.yml by default; override via
/// DBDATASYNC_TEST_ORACLE_SERVER. Unlike <c>PostgresTestDatabase</c>/<c>MySqlFamilyTestDatabase</c>,
/// this does **not** create and drop a fresh database per test class — Oracle has no equivalent an
/// ordinary app user can create cheaply (a new pluggable database is a DBA-level, heavyweight
/// operation, not a per-test-class thing). Isolation instead comes the same way it already does
/// *within* one Postgres/MySQL test database: a GUID-suffixed table name per test, dropped by whichever
/// test created it.
/// </summary>
public sealed class OracleTestDatabase : IAsyncLifetime
{
    public static string ServerConnectionString { get; } =
        Environment.GetEnvironmentVariable("DBDATASYNC_TEST_ORACLE_SERVER")
        ?? "Data Source=localhost:15210/FREEPDB1;User Id=dbdatasync;Password=DbDataSync_Test_Pw1;";

    /// <summary>The connected app user's own schema — confirmed live via <c>SELECT USER FROM DUAL</c>
    /// rather than hardcoded, so a differently-configured server doesn't silently mismatch.</summary>
    public string SchemaName { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        await using var connection = OpenConnection();
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT USER FROM DUAL";
        SchemaName = (string)(await cmd.ExecuteScalarAsync())!;
    }

    public Task DisposeAsync() => Task.CompletedTask;

    public OracleConnection OpenConnection()
    {
        var connection = new OracleConnection(ServerConnectionString);
        connection.Open();
        return connection;
    }
}
