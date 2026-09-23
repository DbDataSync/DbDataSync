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

    /// <summary>
    /// One dictionary per category, keyed by <see cref="GenericDriverKinds"/> — <c>Build*</c> below look
    /// a kind up here rather than switching on it directly, and <see cref="SupportedReaderKinds"/>/
    /// <see cref="SupportedStagingKinds"/>/<see cref="SupportedWriterKinds"/> are these dictionaries' own
    /// <c>.Keys</c>, not a separately maintained list. Worth doing, not just proposing it, because a
    /// `switch` and a hand-written list beside it can silently disagree — found for real while building
    /// the driver-authoring UI's own "don't hardcode this in TypeScript" endpoint:
    /// <see cref="GenericDriverKinds.KeyReconcileScd2Close"/> had no case here at all even though
    /// <c>PostgresDriver</c> constructs it directly (its own hand-written <c>Writers</c> list, identical
    /// constructor shape) — a descriptor-driven <c>driver.yaml</c> listing it in <c>writers:</c> would
    /// have thrown <see cref="ArgumentException"/> at construction. Fixed by adding the entry below, not
    /// by excluding it — nothing about its construction is engine-specific, so there was no reason for
    /// the generic path to be missing what the hand-written one already had.
    /// </summary>
    private static readonly Dictionary<string, Func<TSpec, ISegmentValueBinder, IChangeReader>> ReaderFactories = new()
    {
        [GenericDriverKinds.Watermark] = (spec, binder) => new WatermarkReader(spec.Dialect, binder),
        [GenericDriverKinds.BatchReload] = (spec, binder) => new BatchReloadReader(spec.Dialect, binder),
        [GenericDriverKinds.TriggerAudit] = (spec, _) => new TriggerAuditReader(spec.Dialect, spec.Catalog),
        [GenericDriverKinds.KeyReconcile] = (spec, binder) => new KeyReconcileReader(spec.Dialect, binder),
    };

    private static readonly Dictionary<string, Func<TSpec, IStagingProvider>> StagingFactories = new()
    {
        [GenericDriverKinds.StagingTable] = spec => new BatchInsertStagingProvider(spec.Dialect, spec.Catalog),
    };

    private static readonly Dictionary<string, Func<TSpec, ISegmentValueBinder, IChangeWriter>> WriterFactories = new()
    {
        [GenericDriverKinds.DeleteInsert] = (spec, binder) => new DeleteInsertWriter(spec.Dialect, spec.Catalog, binder),
        [GenericDriverKinds.KeyReconcileDelete] = (spec, binder) => new KeyReconcileDeleteWriter(spec.Dialect, spec.Catalog, binder),
        [GenericDriverKinds.Snapshot] = (spec, _) => new SnapshotWriter(spec.Dialect, spec.Catalog),
        [GenericDriverKinds.Scd2] = (spec, _) => new Scd2Writer(spec.Dialect, spec.Catalog),
        [GenericDriverKinds.KeyReconcileScd2Close] = (spec, binder) => new KeyReconcileScd2CloseWriter(spec.Dialect, spec.Catalog, binder),
    };

    /// <summary>Every <c>GenericDriverKinds</c> value a <c>driver.yaml</c>'s <c>capabilities.readers</c>/
    /// <c>.staging</c>/<c>.writers</c> may actually name — what <c>GET /api/known-driver-kinds</c> (the
    /// driver-authoring UI's checkbox source) reports, so the SPA can never offer a kind
    /// <see cref="BuildWriters"/> (or its reader/staging siblings) would reject.</summary>
    public static IReadOnlyList<string> SupportedReaderKinds => ReaderFactories.Keys.ToList();
    public static IReadOnlyList<string> SupportedStagingKinds => StagingFactories.Keys.ToList();
    public static IReadOnlyList<string> SupportedWriterKinds => WriterFactories.Keys.ToList();

    private static IReadOnlyList<IChangeReader> BuildReaders(TSpec spec, ISegmentValueBinder binder) =>
        spec.Readers.Select(kind => ReaderFactories.TryGetValue(kind, out var factory)
            ? factory(spec, binder)
            : throw new ArgumentException($"'{kind}' is not a generic reader Kind.", nameof(spec)))
            .ToList();

    private static IReadOnlyList<IStagingProvider> BuildStaging(TSpec spec) =>
        spec.Staging.Select(kind => StagingFactories.TryGetValue(kind, out var factory)
            ? factory(spec)
            : throw new ArgumentException($"'{kind}' is not a generic staging Kind.", nameof(spec)))
            .ToList();

    private static IReadOnlyList<IChangeWriter> BuildWriters(TSpec spec, ISegmentValueBinder binder) =>
        spec.Writers.Select(kind => WriterFactories.TryGetValue(kind, out var factory)
            ? factory(spec, binder)
            : throw new ArgumentException($"'{kind}' is not a generic writer Kind.", nameof(spec)))
            .ToList();

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
    /// <remarks>
    /// Phase 176M widened this from <c>catch (DbException ex)</c> — a raw <c>java.sql.SQLException</c>
    /// escaping <em>past</em> JDBC's own ADO.NET boundary used to reach here uncaught, becoming an
    /// unhandled 500 at the API layer instead of a reported <c>Succeeded: false</c>. That escape is now
    /// fixed at its actual source (<c>JdbcCommand</c>'s execute methods and <c>JdbcDataReader.Read</c>
    /// translate every <c>java.sql.SQLException</c> into <c>Jdbc.Ado.JdbcSqlException</c>, a plain
    /// <see cref="DbException"/> subtype, before it ever leaves <c>DbDataSync.Drivers.Jdbc</c>) — this
    /// catch stays widened anyway as a general safety net for any *other* provider's non-<see cref="DbException"/>
    /// failure, not because JDBC specifically still needs it. <see cref="OperationCanceledException"/>
    /// stays excluded deliberately — a cancelled test request is not a "connection failed" answer, and
    /// conflating the two would misreport what happened.
    /// <para>
    /// <c>ex.ToString()</c>, not <c>ex.Message</c> — this project has no compile-time visibility into
    /// <c>DbDataSync.Drivers.Jdbc</c> (would mean depending on it, which itself depends on this project —
    /// a cycle) and doesn't need any: by the time a JDBC failure reaches here it is already a plain
    /// <c>DbException</c> whose own <c>Message</c> already carries the <c>SQLState</c>/<c>ErrorCode</c>/
    /// chained-message detail (built once, at the translation site) — <c>ToString()</c> is just the
    /// ordinary, provider-agnostic choice that also keeps an inner exception's own message, which a
    /// wrapped <see cref="InvalidOperationException"/> (as every phase-175M connect-time validation
    /// throws) would otherwise lose.
    /// </para>
    /// </remarks>
    public async Task<ConnectionTestResult> TestAsync(DbConnection connection, CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        try
        {
            using var cmd = connection.CreateTimedCommand();
            cmd.CommandText = "SELECT 1";
            await cmd.ExecuteScalarAsync(cancellationToken);
            return new ConnectionTestResult(true, Stopwatch.GetElapsedTime(started), connection.ServerVersion, null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new ConnectionTestResult(false, Stopwatch.GetElapsedTime(started), null, ex.ToString());
        }
    }
}
