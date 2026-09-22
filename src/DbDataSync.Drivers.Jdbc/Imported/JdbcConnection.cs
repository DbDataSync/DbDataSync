using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;

namespace DbDataSync.Drivers.Jdbc.Imported;

// Adapted from ClrKernel.Database.Provider.Jdbc (Apache-2.0, github.com/ClrKernel/ClrKernel,
// src/ClrKernel.Database.Provider.Jdbc/JdbcConnection.cs) — phase 165V. Wraps a java.sql.Connection as
// an ADO.NET DbConnection so DbDataSync.Drivers.Generic's readers can drive it exactly like any other
// DbConnection.
internal sealed class JdbcConnection : DbConnection
{
    private java.sql.Connection? _connection;

    [AllowNull] public override string ConnectionString { get; set; } = "";

    public override int ConnectionTimeout => Underlying.getNetworkTimeout() / 1000;

    public override ConnectionState State =>
        _connection != null && !_connection.isClosed() ? ConnectionState.Open : ConnectionState.Closed;

    public override string Database => Underlying.getCatalog();

    public override void ChangeDatabase(string databaseName) => Underlying.setCatalog(databaseName);

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
        _connection = factory.GetJdbcConnection(jdbcUrl, builder.GetProperties());
    }

    public override void Close()
    {
        _connection?.close();
        _connection = null;
    }

    /// <summary>The wrapped <c>java.sql.Connection</c>, for the catalog/driver code in this project that
    /// needs to reach past ADO.NET's own surface (<c>DatabaseMetaData</c> browsing, principally).</summary>
    internal java.sql.Connection Underlying =>
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
