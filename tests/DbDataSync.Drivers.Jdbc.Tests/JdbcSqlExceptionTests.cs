using DbDataSync.Core.Config;
using DbDataSync.Core.Sql;
using DbDataSync.Drivers.Jdbc.Ado;

namespace DbDataSync.Drivers.Jdbc.Tests;

/// <summary>
/// Phase 176M (corrected): every real <c>java.sql.SQLException</c> this driver can throw gets translated
/// to <see cref="JdbcSqlException"/> — a plain <see cref="System.Data.Common.DbException"/> — before it
/// ever leaves this project, rather than shared, non-JDBC-aware code downstream (originally
/// <c>DbDataSync.Api</c>'s own diagnostics helper) having to touch <c>java.sql</c> types itself to make
/// sense of one. Driven against the same real Postgres fixture <see cref="JdbcConnectionTests"/> uses,
/// with genuinely bad SQL rather than a hand-built exception — proves the translation against a real
/// chained <c>java.sql.SQLException</c>, not an assumption about its shape.
/// </summary>
[Trait("Category", "Integration")]
public sealed class JdbcSqlExceptionTests(JdbcTestDatabase db) : IClassFixture<JdbcTestDatabase>, IAsyncLifetime
{
    private JdbcGenericDriver _driver = null!;
    private System.Data.Common.DbConnection _connection = null!;

    public Task InitializeAsync()
    {
        var jarPath = Path.Combine(AppContext.BaseDirectory, "postgresql.jar");
        _driver = new JdbcGenericDriver(new JdbcDriverSpec(
            "jdbc-sqlexception-test", JdbcDialect.Instance, JdbcCatalog.Instance, "org.postgresql.Driver", [jarPath],
            Readers: [], Staging: [], Writers: []));

        var config = new ConnectionConfig
        {
            Name = "jdbc-sqlexception-test",
            DriverType = "Jdbc",
            ConnectionString = $"{JdbcTestDatabase.JdbcUrl}{db.DatabaseName}",
            AuthMode = AuthMode.SqlAuth,
            UserId = "dbdatasync",
        };
        _connection = _driver.CreateConnection(config, "DbDataSync_Test_Pw1");
        _connection.Open();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync() => await _connection.DisposeAsync();

    /// <summary>Genuinely bad SQL — Postgres reports this as SQLState 42601 (syntax_error). The point is
    /// the exception *type*: a raw <c>java.sql.SQLException</c> would fail this test by never satisfying
    /// <c>Assert.Throws&lt;JdbcSqlException&gt;</c> at all (it'd be an uncaught, unrelated exception type
    /// to xUnit).</summary>
    [Fact]
    public void ExecuteNonQuery_WithInvalidSql_ThrowsJdbcSqlException_NotARawJavaException()
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "this is not valid sql at all";

        var ex = Assert.Throws<JdbcSqlException>(() => cmd.ExecuteNonQuery());

        Assert.NotEmpty(ex.Errors);
        Assert.Equal("42601", ex.Errors[0].SqlState);
        Assert.Contains("42601", ex.Message);
    }

    [Fact]
    public async Task ExecuteReaderAsync_WithInvalidSql_ThrowsJdbcSqlException()
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "select * from a_table_that_does_not_exist_anywhere";

        var ex = await Assert.ThrowsAsync<JdbcSqlException>(() => cmd.ExecuteReaderAsync());

        Assert.NotEmpty(ex.Errors);
        // Postgres: 42P01 = undefined_table.
        Assert.Equal("42P01", ex.Errors[0].SqlState);
    }
}
