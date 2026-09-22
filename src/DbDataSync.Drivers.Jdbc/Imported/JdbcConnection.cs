using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;

namespace DbDataSync.Drivers.Jdbc.Imported;

// Adapted from ClrKernel.Database.Provider.Jdbc (Apache-2.0, github.com/ClrKernel/ClrKernel,
// src/ClrKernel.Database.Provider.Jdbc/JdbcConnection.cs) — phase 165V. Wraps a java.sql.Connection as
// an ADO.NET DbConnection so DbDataSync.Drivers.Generic's readers can drive it exactly like any other
// DbConnection.
//
// Public — phase 167V. There is no reason a metadataProvider script (compiled in a different assembly,
// via MetadataContext.Connection) should be unable to name this type or reach the real JDBC objects
// behind it: JavaSqlDriver/JavaSqlConnection below are public for exactly that, named after the literal
// Java type each one returns rather than a project-specific synonym.
public sealed class JdbcConnection : DbConnection
{
    private java.sql.Connection? _connection;

    [AllowNull] public override string ConnectionString { get; set; } = "";

    public override int ConnectionTimeout => JavaSqlConnection.getNetworkTimeout() / 1000;

    public override ConnectionState State =>
        _connection != null && !_connection.isClosed() ? ConnectionState.Open : ConnectionState.Closed;

    public override string Database => JavaSqlConnection.getCatalog();

    public override void ChangeDatabase(string databaseName) => JavaSqlConnection.setCatalog(databaseName);

    // Not implemented: connection *testing* (phase 19) is out of scope for this reader spike (phase
    // 165V) and is the only thing that would need these. DatabaseMetaData.getURL()/
    // getDatabaseProductVersion() are cheap to add when that phase actually needs them.
    public override string DataSource => throw new NotImplementedException(
        "JdbcConnection.DataSource: deferred to a connection-testing phase (see phase 19); " +
        "DatabaseMetaData.getURL() is the answer when that phase needs it.");

    public override string ServerVersion => throw new NotImplementedException(
        "JdbcConnection.ServerVersion: deferred to a connection-testing phase (see phase 19); " +
        "DatabaseMetaData.getDatabaseProductVersion() is the answer when that phase needs it.");

    // Out of scope for a reader (see phase 165V's own planning doc reference): every generic *writer*
    // opens a transaction, but WatermarkReader/BatchReloadReader/TriggerAuditReader do not.
    protected override DbTransaction BeginDbTransaction(IsolationLevel il) => throw new NotImplementedException(
        "JdbcConnection.BeginDbTransaction: writers are out of scope for the reader-only JDBC driver (phase 165V).");

    public override void Open()
    {
        if (_connection != null)
            return;

        var builder = new JdbcConnectionStringBuilder(ConnectionString);
        var driverClass = builder.JdbcDriver
            ?? throw new InvalidOperationException("The connection string must set JdbcDriver.");
        var factory = JdbcProviderFactory.FindByDriver(driverClass)
            ?? throw new InvalidOperationException(
                $"No JdbcProviderFactory is registered for driver class '{driverClass}'. " +
                "Load the driver (JdbcProviderFactory.FromJarPath/FromAssemblyPath) before opening a connection.");
        var jdbcUrl = builder.JdbcUrl
            ?? throw new InvalidOperationException("The connection string must set JdbcUrl.");
        JavaSqlDriver = factory.JdbcDriver;
        _connection = factory.GetJdbcConnection(jdbcUrl, builder.GetProperties());
    }

    public override void Close()
    {
        _connection?.close();
        _connection = null;
        JavaSqlDriver = null;
    }

    /// <summary>
    /// The real <c>java.sql.Driver</c> this connection was opened through — the same reference
    /// <see cref="JdbcProviderFactory.JdbcDriver"/> resolves at <see cref="Open"/> time. Public — phase
    /// 167V — so a <c>metadataProvider</c> script (or anything else with a reference to this type) can
    /// use it directly: <c>getMajorVersion()</c>, <c>getPropertyInfo(url, info)</c>, and so on, with no
    /// DbDataSync-authored wrapper in between.
    /// </summary>
    public java.sql.Driver? JavaSqlDriver { get; private set; }

    /// <summary>
    /// The wrapped <c>java.sql.Connection</c> — named after the literal Java type it returns, not
    /// "Underlying" or another project-specific synonym, so it reads plainly next to
    /// <see cref="JavaSqlDriver"/>. Public — phase 167V — for the same reason: a script (or this
    /// project's own catalog code) reaches <c>getMetaData()</c> and everything else
    /// <c>java.sql.Connection</c> offers directly, no curated passthrough API to design or maintain.
    /// </summary>
    public java.sql.Connection JavaSqlConnection =>
        _connection ?? throw new InvalidOperationException("The connection is not open.");

    protected override DbCommand CreateDbCommand() =>
        State == ConnectionState.Open
            ? new JdbcCommand(this)
            : throw new InvalidOperationException("Connection is closed.");

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            Close();
        base.Dispose(disposing);
    }
}
