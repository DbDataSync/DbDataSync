using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;

namespace DbDataSync.Drivers.Jdbc.Ado;

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

    /// <summary>
    /// Phase 171V — this was <c>throw new NotImplementedException(...)</c>, "deferred to a connection-testing
    /// phase," until <see cref="DbDataSync.Drivers.Generic.GenericDriverBase{TSpec}.TestAsync"/> (phase 168V)
    /// started calling <see cref="ServerVersion"/> unconditionally on a successful test — a
    /// <c>NotImplementedException</c> isn't a <see cref="DbException"/>, so it wasn't caught there, and
    /// every JDBC "Test Connection" crashed after the query that proved the connection worked. Both reads
    /// are defined to never fail once a connection is open (unlike <c>setCatalog</c> — see
    /// <see cref="ChangeDatabase"/>'s own doc comment — these are informational, not a request a driver has
    /// a reason to refuse), so neither needs that method's try/catch-and-translate treatment.
    /// </summary>
    public override string DataSource => JavaSqlConnection.getMetaData().getURL();

    public override string ServerVersion => JavaSqlConnection.getMetaData().getDatabaseProductVersion();

    /// <summary>
    /// Phase 172V — was <c>throw new NotImplementedException(...)</c>, "writers are out of scope for the
    /// reader-only JDBC driver (phase 165V)." <c>java.sql.Connection</c> carries transaction state on the
    /// connection itself, not per-statement the way SQL Server's client library does — every statement run
    /// while <c>autoCommit == false</c> is implicitly part of the open transaction, which is why
    /// <see cref="JdbcCommand"/>'s own <c>DbTransaction</c> setter needs to do nothing beyond storing the
    /// value for the ADO.NET contract to round-trip.
    /// </summary>
    protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel)
    {
        JavaSqlConnection.setAutoCommit(false);
        if (isolationLevel != IsolationLevel.Unspecified)
        {
            try
            {
                JavaSqlConnection.setTransactionIsolation(JavaTransactionIsolation(isolationLevel));
            }
            catch (java.sql.SQLException ex)
            {
                JavaSqlConnection.setAutoCommit(true);
                throw new InvalidOperationException(
                    $"This JDBC driver rejected isolation level '{isolationLevel}' " +
                    $"(java.sql.Connection.setTransactionIsolation threw: {ex.Message}). " +
                    "Leave the isolation level unspecified to use this driver's own default.", ex);
            }
        }
        return new JdbcTransaction(this, isolationLevel);
    }

    /// <summary>Every generic writer in this codebase calls <c>BeginTransactionAsync</c> with no level
    /// argument — always <see cref="IsolationLevel.Unspecified"/> today, which never reaches this method
    /// at all (see <see cref="BeginDbTransaction"/>'s own guard). Built for contract-completeness, not
    /// because anything here currently exercises it.</summary>
    private static int JavaTransactionIsolation(IsolationLevel level) => level switch
    {
        IsolationLevel.ReadUncommitted => java.sql.Connection.TRANSACTION_READ_UNCOMMITTED,
        IsolationLevel.ReadCommitted => java.sql.Connection.TRANSACTION_READ_COMMITTED,
        IsolationLevel.RepeatableRead => java.sql.Connection.TRANSACTION_REPEATABLE_READ,
        IsolationLevel.Serializable => java.sql.Connection.TRANSACTION_SERIALIZABLE,
        _ => throw new NotSupportedException(
            $"'{level}' has no java.sql.Connection.TRANSACTION_* equivalent — " +
            "ReadUncommitted/ReadCommitted/RepeatableRead/Serializable are the only levels JDBC expresses."),
    };

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

        // Credential no longer readable back out via ConnectionString once it's done its job — the same
        // posture SqlConnection's own default (Persist Security Info=false) takes, and the reason to take
        // it here too: JdbcConnection is deliberately public (phase 167V) so a metadataProvider script can
        // hold a direct reference via MetadataContext.Connection, which makes "a caller holding an
        // already-open connection" a real case, not a hypothetical one.
        builder.Remove("user");
        builder.Remove("password");
        ConnectionString = builder.ConnectionString;
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
