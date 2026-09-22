using System.Data.Common;
using System.Diagnostics;
using DbDataSync.Core.Config;
using DbDataSync.Core.Sql;
using DbDataSync.Drivers.Abstractions;

namespace DbDataSync.Drivers.Generic;

/// <summary>
/// An <see cref="IDriver"/> built entirely from a <see cref="GenericDriverSpec"/> — no engine-specific
/// C#. <see cref="Drivers.Postgres.PostgresDriver"/>'s own doc comment already said a new engine is "a
/// dialect, a connection factory and a catalog"; this is that claim made into a class anyone can
/// construct, rather than a new driver project per engine that happens to need nothing but the generic
/// pipeline. It is what phase 109d's YAML descriptor deserialises into, and what a hand-written
/// registration (this phase) can already stand up today.
/// </summary>
public sealed class GenericDriver : IDriver, IConnectionTester, IDialectProvider, ITableCatalogProvider
{
    private readonly GenericDriverSpec _spec;

    public GenericDriver(GenericDriverSpec spec)
    {
        _spec = spec;
        var binder = spec.ValueBinder ?? new GenericValueBinder(spec.Dialect, spec.ProviderFactory);
        Readers = BuildReaders(spec, binder);
        StagingProviders = BuildStaging(spec);
        Writers = BuildWriters(spec, binder);
    }

    /// <summary>The catalog this driver's own components use, for anything composing a generic
    /// component for this engine from outside the driver.</summary>
    public ITableCatalog Catalog => _spec.Catalog;

    /// <summary>What a script generating SQL for this engine is told about it.</summary>
    public SqlDialect Dialect => _spec.Dialect;

    public string DriverType => _spec.Id;

    public string DisplayName => _spec.DisplayName ?? _spec.Id;

    public int? DefaultPort => _spec.DefaultPort;

    public IReadOnlyList<IChangeReader> Readers { get; }
    public IReadOnlyList<IStagingProvider> StagingProviders { get; }
    public IReadOnlyList<IChangeWriter> Writers { get; }

    private static IReadOnlyList<IChangeReader> BuildReaders(GenericDriverSpec spec, ISegmentValueBinder binder)
    {
        var readers = new List<IChangeReader>();
        foreach (var kind in spec.Readers)
        {
            readers.Add(kind switch
            {
                GenericDriverKinds.Watermark => new WatermarkReader(spec.Dialect, binder),
                GenericDriverKinds.BatchReload => new BatchReloadReader(spec.Dialect, binder),
                GenericDriverKinds.TriggerAudit => new TriggerAuditReader(spec.Dialect, spec.Catalog),
                GenericDriverKinds.KeyReconcile => new KeyReconcileReader(spec.Dialect, binder),
                _ => throw new ArgumentException($"'{kind}' is not a generic reader Kind.", nameof(spec)),
            });
        }
        return readers;
    }

    private static IReadOnlyList<IStagingProvider> BuildStaging(GenericDriverSpec spec)
    {
        var staging = new List<IStagingProvider>();
        foreach (var kind in spec.Staging)
        {
            staging.Add(kind switch
            {
                GenericDriverKinds.StagingTable => new BatchInsertStagingProvider(spec.Dialect, spec.Catalog),
                _ => throw new ArgumentException($"'{kind}' is not a generic staging Kind.", nameof(spec)),
            });
        }
        return staging;
    }

    private static IReadOnlyList<IChangeWriter> BuildWriters(GenericDriverSpec spec, ISegmentValueBinder binder)
    {
        var writers = new List<IChangeWriter>();
        foreach (var kind in spec.Writers)
        {
            writers.Add(kind switch
            {
                GenericDriverKinds.DeleteInsert => new DeleteInsertWriter(spec.Dialect, spec.Catalog, binder),
                GenericDriverKinds.KeyReconcileDelete => new KeyReconcileDeleteWriter(spec.Dialect, spec.Catalog, binder),
                GenericDriverKinds.Snapshot => new SnapshotWriter(spec.Dialect, spec.Catalog),
                GenericDriverKinds.Scd2 => new Scd2Writer(spec.Dialect, spec.Catalog),
                _ => throw new ArgumentException($"'{kind}' is not a generic writer Kind.", nameof(spec)),
            });
        }
        return writers;
    }

    /// <summary>
    /// Assembles the connection string through a plain <see cref="DbConnectionStringBuilder"/> —
    /// key/value pairs by name, not a provider-typed builder — using the key spellings
    /// <see cref="GenericDriverSpec.ConnectionStringKeys"/> declares. The credential goes on top of an
    /// operator-supplied connection string exactly as every compiled driver does: config never carries
    /// it, and it is escaped correctly by going through the builder rather than being concatenated.
    /// </summary>
    public DbConnection CreateConnection(ConnectionConfig connection, string? credential)
    {
        var keys = _spec.ConnectionStringKeys;
        var builder = new DbConnectionStringBuilder();
        if (!string.IsNullOrWhiteSpace(connection.ConnectionString))
            builder.ConnectionString = connection.ConnectionString;
        else
            builder[keys.Host] = connection.Host;

        builder[keys.Database] = connection.Database ?? _spec.DefaultDatabase;

        if (connection.Port is int port && keys.Port is not null)
            builder[keys.Port] = port;

        if (connection.ConnectTimeoutSeconds is int connectTimeout)
            builder[keys.ConnectTimeout] = connectTimeout;
        else if (!ConnectionTimeouts.AddressCarriesOwnConnectTimeout(connection, keys.ConnectTimeout))
            builder[keys.ConnectTimeout] = ConnectionTimeouts.DefaultConnectSeconds;

        if (connection.AuthMode == AuthMode.None)
        {
            // Whatever the address or the environment provides. DbDataSync adds nothing.
        }
        else if (connection.AuthMode == AuthMode.IntegratedAuth)
        {
            if (keys.IntegratedSecurity is not null)
            {
                builder[keys.IntegratedSecurity] = true;
            }
            else
            {
                // No integrated-auth flag on this engine (Postgres-style GSSAPI/peer auth): a username
                // is still required, DbDataSync supplies nothing else.
                builder[keys.Username] = connection.UserId
                    ?? throw new InvalidOperationException($"UserId is required even for IntegratedAuth on '{_spec.Id}'.");
            }
        }
        else
        {
            builder[keys.Username] = connection.UserId
                ?? throw new InvalidOperationException("UserId is required for SqlAuth connections.");
            builder[keys.Password] = credential
                ?? throw new InvalidOperationException("A resolved credential is required for SqlAuth connections.");
        }

        foreach (var (key, value) in connection.Properties)
            builder[key] = value;

        var providerConnection = _spec.ProviderFactory.CreateConnection()
            ?? throw new InvalidOperationException($"The provider factory for '{_spec.Id}' did not produce a connection.");
        providerConnection.ConnectionString = builder.ConnectionString;
        return providerConnection.WithCommandTimeout(connection);
    }

    /// <summary>
    /// The ADO.NET-standard <c>Databases</c> schema collection, which every provider built on
    /// <see cref="DbConnection.GetSchema(string)"/> implements — the one catalog operation
    /// <c>information_schema</c> itself cannot answer (it describes the *current* database, not the
    /// server). Column name varies by provider (<c>database_name</c> is Npgsql's; SqlClient's is the
    /// same), so this reads the schema table's first column rather than naming one.
    /// </summary>
    public async Task<IReadOnlyList<string>> ListDatabasesAsync(DbConnection connection, CancellationToken cancellationToken)
    {
        var schema = await connection.GetSchemaAsync("Databases", cancellationToken);
        var results = new List<string>();
        foreach (System.Data.DataRow row in schema.Rows)
            results.Add(Convert.ToString(row[0]) ?? "");
        return results;
    }

    public async Task<IReadOnlyList<TableMetadata>> ListTablesAsync(
        DbConnection connection, string database, CancellationToken cancellationToken)
    {
        await _spec.Dialect.UseDatabaseAsync(connection, database, cancellationToken);
        return await _spec.Catalog.ListTablesAsync(connection, cancellationToken);
    }

    public async Task<IReadOnlyList<ColumnMetadata>> ListColumnsAsync(
        DbConnection connection, string database, string schema, string table, CancellationToken cancellationToken)
    {
        await _spec.Dialect.UseDatabaseAsync(connection, database, cancellationToken);
        return await _spec.Catalog.GetColumnsAsync(connection, schema, table, cancellationToken);
    }

    /// <summary>Round-trips <c>SELECT 1</c> — no user object, no permission beyond connecting, and
    /// portable across every engine this driver could plausibly stand up against.</summary>
    public async Task<ConnectionTestResult> TestAsync(DbConnection connection, CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        try
        {
            using var cmd = connection.CreateTimedCommand();
            cmd.CommandText = "SELECT 1;";
            await cmd.ExecuteScalarAsync(cancellationToken);
            return new ConnectionTestResult(true, Stopwatch.GetElapsedTime(started), connection.ServerVersion, null);
        }
        catch (DbException ex)
        {
            return new ConnectionTestResult(false, Stopwatch.GetElapsedTime(started), null, ex.Message);
        }
    }
}
