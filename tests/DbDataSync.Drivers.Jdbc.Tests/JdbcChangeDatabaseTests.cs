using DbDataSync.Core.Config;
using DbDataSync.Core.Sql;
using DbDataSync.Drivers.Generic;
using DbDataSync.Drivers.Jdbc.Imported;

namespace DbDataSync.Drivers.Jdbc.Tests;

/// <summary>
/// The two halves of the fix for "JDBC drivers aren't required to implement <c>setCatalog</c>": an
/// empty database never calls it at all (no <c>getCatalog()</c> dependency to decide that — a pure
/// check against config already in hand), and a real refusal is translated into the same actionable
/// shape <c>DescriptorDialect</c>'s <c>supportsChangeDatabase: false</c> already gives, rather than a
/// raw <c>java.sql.SQLException</c> surfacing mid-read.
/// </summary>
[Trait("Category", "Integration")]
public sealed class JdbcChangeDatabaseTests(JdbcTestDatabase db) : IClassFixture<JdbcTestDatabase>, IAsyncLifetime
{
    private JdbcGenericDriver _driver = null!;
    private System.Data.Common.DbConnection _connection = null!;

    public Task InitializeAsync()
    {
        var jarPath = Path.Combine(AppContext.BaseDirectory, "postgresql.jar");
        _driver = new JdbcGenericDriver(new JdbcDriverSpec(
            "jdbc-changedb-test", JdbcDialect.Instance, JdbcCatalog.Instance, "org.postgresql.Driver", [jarPath],
            Readers: [], Staging: [], Writers: []));

        var config = new ConnectionConfig
        {
            Name = "jdbc-changedb-test",
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

    [Fact]
    public async Task UseDatabaseAsync_WithAnEmptyDatabase_NeverCallsChangeDatabase()
    {
        // If this touched setCatalog at all, changing to "" would fail — pgJDBC rejects an empty
        // catalog name. Reaching here without an exception is the proof the call was skipped.
        await JdbcDialect.Instance.UseDatabaseAsync(_connection, "", CancellationToken.None);
    }

    [Fact]
    public void ChangeDatabase_OnAConnectionClosedFromUnderneath_IsTranslatedIntoAnActionableError()
    {
        // Two things tried first, neither worked: pgJDBC's setCatalog doesn't validate the name at all
        // (a nonexistent database was silently accepted, no exception); closing the ADO.NET connection
        // first makes JdbcConnection.JavaSqlConnection itself throw ("the connection is not open")
        // before setCatalog is ever reached, which is a different, pre-existing guard, not this one.
        //
        // Closing the *Java* connection directly, without going through JdbcConnection.Close(), leaves
        // ADO.NET's own state unaware anything happened — JavaSqlConnection still returns a real
        // (non-null) object, so the call reaches setCatalog, which is where java.sql.Connection's own
        // contract requires every method to fail once close() has been called on it.
        var jdbc = (JdbcConnection)_connection;
        jdbc.JavaSqlConnection.close();

        var ex = Assert.Throws<InvalidOperationException>(() => jdbc.ChangeDatabase(db.DatabaseName));

        // The real java.sql.SQLException is preserved as the cause, not swallowed — pgJDBC's own
        // org.postgresql.util.PSQLException, a real SQLException subclass, not the exact base type.
        Assert.IsAssignableFrom<java.sql.SQLException>(ex.InnerException);
        Assert.Contains("TableSpec.Database", ex.Message);
    }
}
