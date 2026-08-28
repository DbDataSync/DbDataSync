using System.Data.Common;
using DataSync.Core.Config;
using DataSync.Drivers.Abstractions;
using DataSync.Scripting.Abstractions;

namespace DataSync.Scripting;

/// <summary>
/// Applies a connection's bound <see cref="IMetadataProvider"/>, if it has one, in front of the
/// driver's own catalog.
/// <para>
/// Binding is **connection-level only** — see <see cref="ScriptSlots.BindableAt"/>. The replication and
/// mapping levels are not consulted here and could not be: the SPA's pickers ask before a mapping
/// exists.
/// </para>
/// </summary>
public sealed class ScriptedMetadata(ScriptHost scriptHost, ConfigRepository configRepository)
{
    /// <summary>
    /// Null when nothing is bound, so a caller falls straight through to the driver rather than paying
    /// for a wrapper that does nothing.
    /// </summary>
    private (IMetadataProvider Script, ScriptParameters Parameters)? Resolve(string connectionName)
    {
        ConnectionConfig connection;
        try
        {
            connection = configRepository.LoadConnection(connectionName);
        }
        catch (FileNotFoundException)
        {
            return null;
        }

        return scriptHost.ResolveBinding<IMetadataProvider>(
            ScriptSlots.MetadataProvider, connection, task: null, mapping: null);
    }

    public Task<IReadOnlyList<string>> ListDatabasesAsync(
        string connectionName,
        DbConnection connection,
        IDriver driver,
        IScriptDialect dialect,
        CancellationToken cancellationToken)
    {
        var binding = Resolve(connectionName);
        if (binding is null)
            return driver.ListDatabasesAsync(connection, cancellationToken);

        var context = ContextFor(connection, driver, dialect, binding.Value.Parameters);
        return Guarded(
            () => binding.Value.Script.ListDatabasesAsync(context, cancellationToken), "listing databases");
    }

    public Task<IReadOnlyList<TableMetadata>> ListTablesAsync(
        string connectionName,
        DbConnection connection,
        IDriver driver,
        IScriptDialect dialect,
        string database,
        CancellationToken cancellationToken)
    {
        var binding = Resolve(connectionName);
        if (binding is null)
            return driver.ListTablesAsync(connection, database, cancellationToken);

        var context = ContextFor(connection, driver, dialect, binding.Value.Parameters);
        return Guarded(
            () => binding.Value.Script.ListTablesAsync(context, database, cancellationToken),
            $"listing tables in '{database}'");
    }

    public Task<IReadOnlyList<ColumnMetadata>> ListColumnsAsync(
        string connectionName,
        DbConnection connection,
        IDriver driver,
        IScriptDialect dialect,
        string database,
        string schema,
        string table,
        CancellationToken cancellationToken)
    {
        var binding = Resolve(connectionName);
        if (binding is null)
            return driver.ListColumnsAsync(connection, database, schema, table, cancellationToken);

        var context = ContextFor(connection, driver, dialect, binding.Value.Parameters);
        return Guarded(
            () => binding.Value.Script.ListColumnsAsync(context, database, schema, table, cancellationToken),
            $"listing columns of '{schema}.{table}'");
    }

    /// <summary>
    /// The driver's own answers reach the script as delegates rather than as values, so a script that
    /// ignores them costs nothing — which matters for ODBC and JDBC, where the driver's answer may not
    /// work at all, and for the common case, where filtering the driver's list is the whole job.
    /// </summary>
    private static MetadataContext ContextFor(
        DbConnection connection, IDriver driver, IScriptDialect dialect, ScriptParameters parameters) =>
        new(connection,
            dialect,
            parameters,
            ct => driver.ListDatabasesAsync(connection, ct),
            (database, ct) => driver.ListTablesAsync(connection, database, ct),
            (database, schema, table, ct) => driver.ListColumnsAsync(connection, database, schema, table, ct));

    private static async Task<T> Guarded<T>(Func<Task<T>> action, string what)
    {
        try
        {
            return await action();
        }
        catch (Exception ex) when (ex is not ScriptExecutionException)
        {
            throw new ScriptExecutionException($"The metadata provider script threw while {what}: {ex.Message}", ex);
        }
    }
}
