using Microsoft.Data.SqlClient;
using Xunit;

namespace DataSync.Drivers.MsSql.Tests;

/// <summary>
/// Creates a uniquely-named database (with Change Tracking enabled) on a real SQL Server instance
/// for the lifetime of one test class, and drops it afterwards. Points at the local Docker container
/// used during Phase 3 development by default; override via DATASYNC_TEST_MSSQL_SERVER for other
/// environments (e.g. a future CI service container).
/// </summary>
public sealed class MsSqlTestDatabase : IAsyncLifetime
{
    public static string ServerConnectionString { get; } =
        Environment.GetEnvironmentVariable("DATASYNC_TEST_MSSQL_SERVER")
        ?? "Data Source=localhost,14330;User ID=sa;Password=DataSync_Test_Pw1;TrustServerCertificate=True";

    public string DatabaseName { get; } = $"DataSyncTest_{Guid.NewGuid():N}";

    public async Task InitializeAsync()
    {
        await using var connection = new SqlConnection(ServerConnectionString);
        await connection.OpenAsync();

        await ExecuteAsync(connection, $"CREATE DATABASE [{DatabaseName}];");
        await ExecuteAsync(
            connection,
            $"ALTER DATABASE [{DatabaseName}] SET CHANGE_TRACKING = ON (CHANGE_RETENTION = 2 DAYS, AUTO_CLEANUP = OFF);");
    }

    public async Task DisposeAsync()
    {
        await using var connection = new SqlConnection(ServerConnectionString);
        await connection.OpenAsync();
        await ExecuteAsync(connection, $"ALTER DATABASE [{DatabaseName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;");
        await ExecuteAsync(connection, $"DROP DATABASE [{DatabaseName}];");
    }

    public SqlConnection OpenConnection()
    {
        var builder = new SqlConnectionStringBuilder(ServerConnectionString)
        {
            InitialCatalog = DatabaseName,
            MultipleActiveResultSets = true,
        };
        var connection = new SqlConnection(builder.ConnectionString);
        connection.Open();
        return connection;
    }

    private static async Task ExecuteAsync(SqlConnection connection, string sql)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }
}
