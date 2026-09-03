using Microsoft.Data.SqlClient;
using Xunit;

namespace DbDataSync.Drivers.Postgres.Tests;

/// <summary>
/// A throwaway SQL Server database for the cross-engine tests.
/// <para>
/// Deliberately not the MSSQL test project's <c>MsSqlTestDatabase</c>: reaching into another test
/// project would make this one depend on that one's whole test surface, and this fixture needs less
/// anyway — cross-engine replication runs in batch and watermark mode, so there is no Change Tracking
/// to enable.
/// </para>
/// </summary>
public sealed class MsSqlScratchDatabase : IAsyncLifetime
{
    public static string ServerConnectionString { get; } =
        Environment.GetEnvironmentVariable("DBDATASYNC_TEST_MSSQL_SERVER")
        ?? "Data Source=localhost,14330;User ID=sa;Password=DbDataSync_Test_Pw1;TrustServerCertificate=True";

    public string DatabaseName { get; } = $"DbDataSyncXEng_{Guid.NewGuid():N}";

    public async Task InitializeAsync()
    {
        await using var connection = new SqlConnection(ServerConnectionString);
        await connection.OpenAsync();
        await ExecuteAsync(connection, $"CREATE DATABASE [{DatabaseName}];");
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
