using MySqlConnector;
using Xunit;

namespace DbDataSync.Drivers.MySql.Tests;

/// <summary>
/// Creates a uniquely-named database on a real MySQL-family instance for the lifetime of one test class
/// and drops it afterwards — the same shape <c>PostgresTestDatabase</c> uses. Abstract over the server
/// connection string so the identical test bodies in <c>MySqlPipelineTests</c>/<c>MariaDbPipelineTests</c>
/// and <c>MySqlTriggerAuditReaderTests</c>/<c>MariaDbTriggerAuditReaderTests</c> run against both forks
/// without duplicating anything but which server they point at — the empirical half of
/// <c>change-tracking-mysql-and-mariadb-triggers.md</c>'s own "identical DDL, both engines" claim.
/// </summary>
public abstract class MySqlFamilyTestDatabase : IAsyncLifetime
{
    protected abstract string ServerConnectionString { get; }

    /// <summary>Lower-case: neither engine folds unquoted identifiers the way Postgres does, but a
    /// generated name never needs escaping either way, and staying consistent with the Postgres fixture
    /// costs nothing.</summary>
    public string DatabaseName { get; } = $"dbdatasync_test_{Guid.NewGuid():N}";

    public async Task InitializeAsync()
    {
        await using var connection = new MySqlConnection(ServerConnectionString);
        await connection.OpenAsync();
        await ExecuteAsync(connection, $"CREATE DATABASE `{DatabaseName}`;");
    }

    public async Task DisposeAsync()
    {
        await using var connection = new MySqlConnection(ServerConnectionString);
        await connection.OpenAsync();
        await ExecuteAsync(connection, $"DROP DATABASE IF EXISTS `{DatabaseName}`;");
    }

    public MySqlConnection OpenConnection()
    {
        var builder = new MySqlConnectionStringBuilder(ServerConnectionString) { Database = DatabaseName };
        var connection = new MySqlConnection(builder.ConnectionString);
        connection.Open();
        return connection;
    }

    private static async Task ExecuteAsync(MySqlConnection connection, string sql)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }
}

/// <summary>Points at the local <c>mysql</c> Docker container from docker-compose.yml by default;
/// override via DBDATASYNC_TEST_MYSQL_SERVER.</summary>
public sealed class MySqlTestDatabase : MySqlFamilyTestDatabase
{
    protected override string ServerConnectionString { get; } =
        Environment.GetEnvironmentVariable("DBDATASYNC_TEST_MYSQL_SERVER")
        ?? "Server=localhost;Port=13306;UserID=root;Password=DbDataSync_Test_Pw1;AllowUserVariables=true;";
}

/// <summary>Points at the local <c>mariadb</c> Docker container from docker-compose.yml by default;
/// override via DBDATASYNC_TEST_MARIADB_SERVER.</summary>
public sealed class MariaDbTestDatabase : MySqlFamilyTestDatabase
{
    protected override string ServerConnectionString { get; } =
        Environment.GetEnvironmentVariable("DBDATASYNC_TEST_MARIADB_SERVER")
        ?? "Server=localhost;Port=13307;UserID=root;Password=DbDataSync_Test_Pw1;AllowUserVariables=true;";
}
