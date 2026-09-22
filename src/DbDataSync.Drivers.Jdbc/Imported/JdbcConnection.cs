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

    /// <summary>
    /// <c>java.sql.Connection.setCatalog</c> is only ever called here for a database an operator
    /// actually named — <c>SqlDialect.UseDatabaseAsync</c>'s own empty-database check (see
    /// <c>DbDataSync.Core.Config.TableRef.Database</c>'s doc comment) means this method is simply never
    /// reached for a mapping that doesn't need a switch at all, so there is no dependency on
    /// <see cref="Database"/> (<c>getCatalog()</c>) here to decide whether to call this.
    /// <para>
    /// The JDBC spec's own Javadoc for <c>setCatalog</c> says a driver that doesn't support catalogs
    /// "will silently ignore this request" — guidance, not a guarantee every vendor's driver follows,
    /// and this codebase's own premise is that the engine behind an arbitrary JDBC URL is not knowable
    /// at design time. So a real <c>java.sql.SQLException</c> here (including
    /// <c>SQLFeatureNotSupportedException</c>, which extends it) is translated into the same
    /// actionable shape <c>DescriptorDialect.UseDatabaseAsync</c>'s <c>supportsChangeDatabase: false</c>
    /// branch already gives an operator who declared the limitation up front, rather than a raw Java
    /// exception surfacing mid-read where nothing expects one.
    /// </para>
    /// </summary>
    public override void ChangeDatabase(string databaseName)
    {
        try
        {
            JavaSqlConnection.setCatalog(databaseName);
        }
        catch (java.sql.SQLException ex)
        {
            throw new InvalidOperationException(
                $"This JDBC driver rejected switching to database/catalog '{databaseName}' " +
                $"(java.sql.Connection.setCatalog threw: {ex.Message}). If every mapping on this " +
                "connection already targets the same database as the connection's own JDBC URL, leave " +
                "the mapping's Database empty instead of naming it explicitly — see TableSpec.Database's " +
                "own doc comment. If mappings genuinely need different databases and this driver can't " +
                "switch, configure a separate DbDataSync connection per database instead.", ex);
        }
    }

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
