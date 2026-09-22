using DbDataSync.Core.Config;
using DbDataSync.Core.Sql;
using DbDataSync.Drivers.Generic;
using DbDataSync.Drivers.Jdbc.Imported;

namespace DbDataSync.Drivers.Jdbc.Tests;

/// <summary>
/// Phase 171V (<c>ServerVersion</c>/<c>DataSource</c> no longer throw — and, load-bearing, that
/// <c>GenericDriverBase.TestAsync</c> doesn't crash reaching them) and the follow-up fix for
/// <c>JdbcConnection.ConnectionString</c> persisting the password after <c>Open()</c>.
/// </summary>
[Trait("Category", "Integration")]
public sealed class JdbcConnectionTests(JdbcTestDatabase db) : IClassFixture<JdbcTestDatabase>, IAsyncLifetime
{
    private JdbcGenericDriver _driver = null!;
    private System.Data.Common.DbConnection _connection = null!;

    public Task InitializeAsync()
    {
        var jarPath = Path.Combine(AppContext.BaseDirectory, "postgresql.jar");
        _driver = new JdbcGenericDriver(new JdbcDriverSpec(
            "jdbc-connection-test", JdbcDialect.Instance, JdbcCatalog.Instance, "org.postgresql.Driver", jarPath,
            Readers: [], Staging: [], Writers: []));

        var config = new ConnectionConfig
        {
            Name = "jdbc-connection-test",
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
    public void ServerVersion_ReturnsARealVersionString_InsteadOfThrowing()
    {
        Assert.False(string.IsNullOrWhiteSpace(_connection.ServerVersion));
    }

    [Fact]
    public void DataSource_ReturnsTheJdbcUrl_InsteadOfThrowing()
    {
        Assert.Contains("jdbc:postgresql:", _connection.DataSource);
    }

    /// <summary>The regression 171V actually exists to close: before this fix, this crashed with
    /// <c>NotImplementedException</c> — not caught by <c>TestAsync</c>'s own <c>catch (DbException)</c> —
    /// after the "SELECT 1" that proves the connection works, on every JDBC-backed driver.</summary>
    [Fact]
    public async Task TestAsync_OnAWorkingConnection_SucceedsInsteadOfCrashing()
    {
        var result = await _driver.TestAsync(_connection, CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Null(result.Error);
        Assert.False(string.IsNullOrWhiteSpace(result.ServerVersion));
    }

    [Fact]
    public void ConnectionString_NoLongerCarriesThePassword_AfterOpen()
    {
        Assert.DoesNotContain("DbDataSync_Test_Pw1", _connection.ConnectionString);
    }

    [Fact]
    public void ConnectionString_StillCarriesTheUrlAndDriver_AfterOpen()
    {
        // Stripping the credential shouldn't take the rest of the round-trippable state with it — a
        // reconnect from the stored string (were anything to do that) still needs these.
        // DbConnectionStringBuilder lowercases keys on round-trip, hence the casing here.
        Assert.Contains("jdbcurl=", _connection.ConnectionString, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("jdbcdriver=", _connection.ConnectionString, StringComparison.OrdinalIgnoreCase);
    }
}
