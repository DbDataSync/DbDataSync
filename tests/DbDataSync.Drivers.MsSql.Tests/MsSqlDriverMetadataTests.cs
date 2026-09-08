using DbDataSync.Core.Config;
using Microsoft.Data.SqlClient;
using Xunit;

namespace DbDataSync.Drivers.MsSql.Tests;

[Trait("Category", "Integration")]
public sealed class MsSqlDriverMetadataTests(MsSqlTestDatabase db) : IClassFixture<MsSqlTestDatabase>, IAsyncLifetime
{
    private readonly MsSqlDriver _driver = new();
    private SqlConnection _connection = null!;
    private string _tableName = null!;

    public async Task InitializeAsync()
    {
        // xUnit creates a fresh test-class instance per [Fact], so InitializeAsync runs once per
        // test method even though the fixture's database is shared across the whole class — the
        // table name must be unique per instance to avoid colliding with a sibling test's table.
        _connection = db.OpenConnection();
        _tableName = $"MetadataProbe_{Guid.NewGuid():N}";

        await using var cmd = _connection.CreateCommand();
        cmd.CommandText = $"""
            CREATE TABLE dbo.[{_tableName}] (
                Id INT NOT NULL PRIMARY KEY,
                Name NVARCHAR(50) NOT NULL,
                Amount DECIMAL(18,2) NULL
            );
            """;
        await cmd.ExecuteNonQueryAsync();
    }

    public Task DisposeAsync()
    {
        _connection.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task CreateConnection_OpensSuccessfullyWithSqlAuth()
    {
        var config = new ConnectionConfig
        {
            Name = "test",
            DriverType = DriverIds.MsSql,
            Host = "localhost",
            Port = 14330,
            Database = db.DatabaseName,
            AuthMode = AuthMode.SqlAuth,
            UserId = "sa",
        };

        using var connection = _driver.CreateConnection(config, "DbDataSync_Test_Pw1");
        await connection.OpenAsync();

        Assert.Equal(System.Data.ConnectionState.Open, connection.State);
    }

    [Fact]
    public async Task ListDatabasesAsync_IncludesTestDatabase()
    {
        var databases = await _driver.ListDatabasesAsync(_connection, CancellationToken.None);
        Assert.Contains(db.DatabaseName, databases);
    }

    [Fact]
    public async Task ListTablesAsync_ReturnsCreatedTable()
    {
        var tables = await _driver.ListTablesAsync(_connection, db.DatabaseName, CancellationToken.None);
        Assert.Contains(tables, t => t.Schema == "dbo" && t.Table == _tableName);
    }

    [Fact]
    public async Task EstimateRowCountAsync_ReflectsRowsPresent_AndIsNullForAMissingTable()
    {
        await using (var insert = _connection.CreateCommand())
        {
            insert.CommandText = $"""
                INSERT INTO dbo.[{_tableName}] (Id, Name) VALUES (1, 'a'), (2, 'b'), (3, 'c'), (4, 'd'), (5, 'e');
                """;
            await insert.ExecuteNonQueryAsync();
        }

        var table = new TableRef { ConnectionName = "test", Database = db.DatabaseName, Schema = "dbo", Table = _tableName };
        var estimate = await _driver.EstimateRowCountAsync(_connection, table, CancellationToken.None);
        Assert.Equal(5, estimate);

        var missing = new TableRef
        {
            ConnectionName = "test", Database = db.DatabaseName, Schema = "dbo", Table = $"NoSuchTable_{Guid.NewGuid():N}",
        };
        Assert.Null(await _driver.EstimateRowCountAsync(_connection, missing, CancellationToken.None));
    }

    [Fact]
    public async Task ListColumnsAsync_ReturnsExpectedColumnsWithTypesAndPrimaryKey()
    {
        var columns = await _driver.ListColumnsAsync(_connection, db.DatabaseName, "dbo", _tableName, CancellationToken.None);

        var id = Assert.Single(columns, c => c.Name == "Id");
        Assert.True(id.IsPrimaryKey);
        Assert.False(id.IsNullable);

        var name = Assert.Single(columns, c => c.Name == "Name");
        Assert.Equal("nvarchar(50)", name.NativeType);
        Assert.False(name.IsPrimaryKey);

        var amount = Assert.Single(columns, c => c.Name == "Amount");
        Assert.Equal("decimal(18,2)", amount.NativeType);
        Assert.True(amount.IsNullable);
    }
}
