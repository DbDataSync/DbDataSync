using System.Data.Common;
using System.Diagnostics;
using DbDataSync.Core.Config;
using DbDataSync.Core.Sql;
using DbDataSync.Drivers.Abstractions;

namespace DbDataSync.Drivers.Generic;

/// <summary>
/// What <see cref="GenericDriverBase{TSpec}"/> needs from a spec — the fields that don't depend on how a
/// connection actually gets made. Phase 168V: split out of the concrete <see cref="GenericDriverSpec"/>
/// so a JDBC-backed spec (<c>JdbcDriverSpec</c>, <c>DbDataSync.Drivers.Jdbc</c>) can share this base
/// without carrying ADO.NET-only fields (<see cref="GenericDriverSpec.ProviderFactory"/>,
/// <see cref="GenericDriverSpec.ConnectionStringKeys"/>) it has no use for.
/// </summary>
public interface IGenericDriverSpec
{
    string Id { get; }
    SqlDialect Dialect { get; }
    IDescriptorCatalog Catalog { get; }
    IReadOnlyList<string> Readers { get; }
    IReadOnlyList<string> Staging { get; }
    IReadOnlyList<string> Writers { get; }
    ISegmentValueBinder? ValueBinder { get; }
    string? DisplayName { get; }
}

/// <summary>
/// Everything a driver built entirely from <c>DbDataSync.Drivers.Generic</c>'s engine-neutral components
/// needs, regardless of how its connection gets made — phase 168V, extracted from what was
/// <see cref="GenericDriver"/> alone. <see cref="GenericDriver"/> (ADO.NET, via a <see
/// cref="DbProviderFactory"/>) and <c>JdbcGenericDriver</c> (<c>DbDataSync.Drivers.Jdbc</c>, via IKVM)
/// both extend this; only <see cref="CreateConnection"/>/<see cref="ListDatabasesAsync"/>/
/// <see cref="SwitchDatabaseAsync"/> differ between them — everything else (which readers/staging/writers
/// to build, how <see cref="ListTablesAsync"/>/<see cref="ListColumnsAsync"/> consult the catalog, the
/// connection round-trip <see cref="TestAsync"/> uses) is identical and lives here once.
/// </summary>
public abstract class GenericDriverBase<TSpec>(TSpec spec, ISegmentValueBinder binder)
    : IDriver, IConnectionTester, IDialectProvider, ITableCatalogProvider
    where TSpec : IGenericDriverSpec
{
    protected TSpec Spec { get; } = spec;

    /// <summary>The catalog this driver's own components use, for anything composing a generic
    /// component for this engine from outside the driver.</summary>
    public ITableCatalog Catalog => Spec.Catalog;

    /// <summary>What a script generating SQL for this engine is told about it.</summary>
    public SqlDialect Dialect => Spec.Dialect;

    public string DriverType => Spec.Id;

    public string DisplayName => Spec.DisplayName ?? Spec.Id;

    public IReadOnlyList<IChangeReader> Readers { get; } = BuildReaders(spec, binder);
    public IReadOnlyList<IStagingProvider> StagingProviders { get; } = BuildStaging(spec);
    public IReadOnlyList<IChangeWriter> Writers { get; } = BuildWriters(spec, binder);

    private static IReadOnlyList<IChangeReader> BuildReaders(TSpec spec, ISegmentValueBinder binder)
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

    private static IReadOnlyList<IStagingProvider> BuildStaging(TSpec spec)
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

    private static IReadOnlyList<IChangeWriter> BuildWriters(TSpec spec, ISegmentValueBinder binder)
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

    public abstract DbConnection CreateConnection(ConnectionConfig connection, string? credential);

    public abstract Task<IReadOnlyList<string>> ListDatabasesAsync(DbConnection connection, CancellationToken cancellationToken);

    /// <summary>Points <paramref name="connection"/> at <paramref name="database"/> before the catalog is
    /// asked anything — <see cref="GenericDriver"/>'s override does, through <see cref="Dialect"/>; a
    /// JDBC connection is bound to one database for its lifetime and overrides this to do nothing (the
    /// same posture <c>PostgresDialect.UseDatabaseAsync</c>'s own doc comment already describes).</summary>
    protected abstract Task SwitchDatabaseAsync(DbConnection connection, string database, CancellationToken cancellationToken);

    public async Task<IReadOnlyList<TableMetadata>> ListTablesAsync(
        DbConnection connection, string database, CancellationToken cancellationToken)
    {
        await SwitchDatabaseAsync(connection, database, cancellationToken);
        return await Spec.Catalog.ListTablesAsync(connection, cancellationToken);
    }

    public async Task<IReadOnlyList<ColumnMetadata>> ListColumnsAsync(
        DbConnection connection, string database, string schema, string table, CancellationToken cancellationToken)
    {
        await SwitchDatabaseAsync(connection, database, cancellationToken);
        return await Spec.Catalog.GetColumnsAsync(connection, schema, table, cancellationToken);
    }

    /// <summary>Round-trips <c>SELECT 1</c> — no user object, no permission beyond connecting, and
    /// portable across every engine either kind could plausibly stand up against.</summary>
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
