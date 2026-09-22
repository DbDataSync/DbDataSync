using System.Data.Common;
using DbDataSync.Core.Config;
using Npgsql;

namespace DbDataSync.Drivers.Jdbc.Tests;

/// <summary>
/// Phase 167V: <see cref="JdbcCatalog"/> now answers from <c>java.sql.DatabaseMetaData</c> rather than
/// <c>InformationSchemaQueries</c>. Exercises it directly against the live Postgres container, asserting
/// the same facts <c>InformationSchemaQueries</c> itself would have reported for an identical table —
/// name, native type (with length/precision/scale assembled from <c>COLUMN_SIZE</c>/<c>DECIMAL_DIGITS</c>,
/// not a separate pair of columns the way <c>information_schema</c> has), nullability, primary key, and
/// identity.
/// </summary>
[Trait("Category", "Integration")]
public sealed class JdbcCatalogTests(JdbcTestDatabase db) : IClassFixture<JdbcTestDatabase>, IAsyncLifetime
{
    private readonly string _tableName = $"jdbc_catalog_spike_{Guid.NewGuid():N}";

    private NpgsqlConnection _npgsql = null!;
    private JdbcDriver _jdbcDriver = null!;
    private DbConnection _jdbc = null!;

    public async Task InitializeAsync()
    {
        _npgsql = db.OpenNpgsqlConnection();
        await ExecuteAsync(_npgsql, $"""
            CREATE TABLE public."{_tableName}" (
                id serial primary key,
                name varchar(50) not null,
                amount numeric(10,2),
                created_at timestamp not null);
            """);

        var jarPath = Path.Combine(AppContext.BaseDirectory, "postgresql.jar");
        _jdbcDriver = new JdbcDriver("org.postgresql.Driver", jarPath);

        var config = new ConnectionConfig
        {
            Name = "jdbc-catalog-test",
            DriverType = "Jdbc",
            ConnectionString = $"{JdbcTestDatabase.JdbcUrl}{db.DatabaseName}",
            AuthMode = AuthMode.SqlAuth,
            UserId = "dbdatasync",
        };
        _jdbc = _jdbcDriver.CreateConnection(config, "DbDataSync_Test_Pw1");
        _jdbc.Open();
    }

    public async Task DisposeAsync()
    {
        _jdbc.Dispose();
        await _npgsql.DisposeAsync();
    }

    private static async Task ExecuteAsync(NpgsqlConnection connection, string sql)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }

    [Fact]
    public async Task ListTablesAsync_FindsTheTable()
    {
        var tables = await JdbcCatalog.Instance.ListTablesAsync(_jdbc, CancellationToken.None);

        Assert.Contains(tables, t => t.Schema == "public" && t.Table == _tableName);
    }

    [Fact]
    public async Task GetColumnsAsync_ReportsTypeNullabilityPrimaryKeyAndIdentity()
    {
        var columns = await JdbcCatalog.Instance.GetColumnsAsync(_jdbc, "public", _tableName, CancellationToken.None);

        var id = columns.Single(c => c.Name == "id");
        Assert.False(id.IsNullable);
        Assert.True(id.IsPrimaryKey);
        // Found by actually running this, not assumed: pgJDBC's DatabaseMetaData detects a
        // nextval(...)-backed default (what `serial` desugars to) and reports IS_AUTOINCREMENT = "YES"
        // for it — real, documented pgJDBC behavior. A genuine improvement over InformationSchemaQueries,
        // which hardcodes IsIdentity: false unconditionally today ("not universally populated... left
        // false here") and would have missed this entirely.
        Assert.True(id.IsIdentity);
        // Also found by running it, not assumed: pgJDBC's TYPE_NAME for this column is the synthesized
        // pseudo-type "serial", not the underlying "int4" — real evidence for why JDBC type-name parsing
        // belongs in a per-engine driver.yaml typeMap (phase 167V's own decision) rather than one
        // hardcoded dialect: an operator's Postgres-via-JDBC typeMap needs "serial"/"bigserial" entries
        // synonymous with their underlying integer types, which is exactly the kind of per-vendor detail
        // this design pushes to the descriptor instead of trying to anticipate centrally.
        Assert.Equal("serial", id.NativeType);

        var name = columns.Single(c => c.Name == "name");
        Assert.False(name.IsNullable);
        Assert.False(name.IsPrimaryKey);
        Assert.Equal("varchar(50)", name.NativeType);

        var amount = columns.Single(c => c.Name == "amount");
        Assert.True(amount.IsNullable);
        Assert.Equal("numeric(10,2)", amount.NativeType);

        var createdAt = columns.Single(c => c.Name == "created_at");
        Assert.False(createdAt.IsNullable);
        Assert.Equal("timestamp", createdAt.NativeType);
    }
}
