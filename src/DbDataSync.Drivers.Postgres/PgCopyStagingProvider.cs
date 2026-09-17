using System.Data.Common;
using DbDataSync.Core.Config;
using DbDataSync.Core.Sql;
using DbDataSync.Drivers.Abstractions;
using DbDataSync.Drivers.Generic;
using Npgsql;
using NpgsqlTypes;

namespace DbDataSync.Drivers.Postgres;

/// <summary>
/// Stages a change set into a real table via <c>COPY … FROM STDIN (FORMAT BINARY)</c> — phase 38.
/// <para>
/// Same table, same DDL, same cleanup as <see cref="BatchInsertStagingProvider"/>, which is the point:
/// only how the rows get in is different, so every writer downstream is unaffected and the two are
/// interchangeable per mapping. The DDL is literally that provider's own builder
/// (<see cref="StagingStatement.BuildCreate"/>), not a copy of it.
/// </para>
///
/// <para>
/// **Why this is a driver-specific provider rather than a dialect hook.** <c>COPY</c> is a protocol on
/// the connection, not a statement: there is nothing for <see cref="SqlDialect"/> to render, and the
/// rows are handed to Npgsql's own importer one typed cell at a time. That is also the whole
/// performance argument — a multi-row <c>INSERT</c> spends a parameter per cell and is bounded by the
/// server's parameter limit, so a wide table stages in small batches however many rows arrived.
/// </para>
///
/// <para>
/// **Binary <c>COPY</c> does no coercion.** A parameterised <c>INSERT</c> lets the server convert a
/// value to the column's type; the binary protocol does not — the wire format has to match the column
/// exactly. So every cell is written with the type the staging column was *declared* with (taken from
/// the target's cached shape, the same place the DDL comes from), and a value the converter will not
/// accept fails the pass with a message naming the column rather than a raw Npgsql error. That is the
/// real difference between this provider and the generic one, and the reason
/// <see cref="GenericDriverKinds.StagingTable"/> stays registered alongside it rather than being
/// replaced by it.
/// </para>
///
/// <para>
/// The commonest way to meet that: a <c>timestamp with time zone</c> column fed a
/// <see cref="DateTime"/> whose <see cref="DateTime.Kind"/> is not <see cref="DateTimeKind.Utc"/>.
/// Npgsql refuses it, and deliberately so — picking a time zone for somebody's data is not a decision
/// a staging provider should make silently. The message says so and names the alternative.
/// </para>
/// </summary>
public sealed class PgCopyStagingProvider(SqlDialect dialect, ITableCatalog catalog)
    : IStagingProvider, IStatementPreview
{
    public string Kind => PostgresDriverKinds.CopyStaging;

    public async Task<StagedChangeSet> StageAsync(
        DbConnection targetConnection,
        TableRef target,
        IAsyncEnumerable<ChangeRow> rows,
        IReadOnlyList<ColumnMapping> columnMappings,
        string mappingName,
        IReadOnlyList<CachedColumn> targetColumns,
        IReadOnlyDictionary<string, string> options,
        CancellationToken cancellationToken)
    {
        if (columnMappings.Count == 0)
            throw new InvalidOperationException("ColumnMappings must be specified to stage changes.");
        if (targetConnection is not NpgsqlConnection npgsqlConnection)
            throw new InvalidOperationException(
                $"'{Kind}' stages through Npgsql's binary COPY and needs an NpgsqlConnection, but the " +
                $"target connection is a {targetConnection.GetType().Name}. Use " +
                $"'{PostgresDriverKinds.StagingTable}', which is engine-neutral.");

        await dialect.UseDatabaseAsync(targetConnection, target.Database, cancellationToken);

        var mappedTargetColumns = columnMappings.Select(m => m.TargetColumn).Distinct().ToList();
        var typeByName = mappedTargetColumns.ToDictionary(
            c => c, c => targetColumns.RequireColumn(mappingName, "target", c).NativeType,
            StringComparer.OrdinalIgnoreCase);

        // Phase 132, as in every staging provider: the two ordering columns are reader-only
        // pass-through columns that the target's own schema cannot state, so whether this table's DDL
        // needs them can only be answered by the first staged row — peeked and replayed, since the
        // table is created before COPY starts.
        var (hasChangeOrdering, effectiveRows) = await ChangeOrdering.DetectAsync(rows, cancellationToken);

        var stagingTable = dialect.QualifyTable(target.Schema, $"DS_STG_{Guid.NewGuid():N}");

        using (var createCmd = targetConnection.CreateTimedCommand())
        {
            createCmd.CommandText = StagingStatement.BuildCreate(
                dialect, stagingTable, mappedTargetColumns, typeByName, hasChangeOrdering);
            await createCmd.ExecuteNonQueryAsync(cancellationToken);
        }

        try
        {
            var rowCount = await CopyAllAsync(
                npgsqlConnection, stagingTable, mappedTargetColumns, typeByName, columnMappings,
                effectiveRows, hasChangeOrdering, cancellationToken);
            return new StagedChangeSet(stagingTable, rowCount, hasChangeOrdering);
        }
        catch
        {
            // The table is real, so a failure part-way through staging leaves it behind unless it is
            // dropped here — the caller only knows to clean up a change set it was handed.
            try
            {
                await DropAsync(targetConnection, stagingTable, CancellationToken.None);
            }
            catch (DbException)
            {
            }
            throw;
        }
    }

    public async Task CleanupAsync(
        DbConnection targetConnection, StagedChangeSet staged, CancellationToken cancellationToken) =>
        await DropAsync(targetConnection, staged.StagingLocation, cancellationToken);

    /// <inheritdoc cref="BatchInsertStagingProvider.DescribeAsync"/>
    public async Task<IReadOnlyList<PreviewStatement>> DescribeAsync(
        PreviewRequest request, CancellationToken cancellationToken)
    {
        await dialect.UseDatabaseAsync(request.Connection, request.Target.Database, cancellationToken);

        var targetColumns = await catalog.GetColumnsAsync(
            request.Connection, request.Target.Schema, request.Target.Table, cancellationToken);
        var typeByName = targetColumns.ToDictionary(c => c.Name, c => c.NativeType, StringComparer.OrdinalIgnoreCase);
        var mapped = request.ColumnMappings.Select(m => m.TargetColumn).Distinct().ToList();

        var missing = mapped.Where(c => !typeByName.ContainsKey(c)).ToList();
        if (missing.Count > 0)
        {
            return
            [
                new PreviewStatement(
                    PreviewStages.Staging, "Create the staging table", null, PreviewOrigin.BuiltIn,
                    $"Mapped column(s) {string.Join(", ", missing)} are not on " +
                    $"'{request.Target.Schema}.{request.Target.Table}', so this pass would fail here."),
            ];
        }

        var stagingTable = dialect.QualifyTable(request.Target.Schema, "DS_STG_<per pass>");
        return
        [
            new PreviewStatement(
                PreviewStages.Staging, "Create the staging table",
                StagingStatement.BuildCreate(dialect, stagingTable, mapped, typeByName), PreviewOrigin.BuiltIn,
                "A real table in the target's own schema — there is no portable scratch namespace — " +
                "dropped when the pass finishes with it."),

            new PreviewStatement(
                PreviewStages.Staging, "Load the rows into it",
                BuildCopyCommand(dialect, stagingTable, mapped, includeChangeOrdering: false), PreviewOrigin.BuiltIn,
                "A binary COPY stream rather than a statement per batch: the text shown is what opens " +
                "it, and the rows follow on the same connection as typed values, not as parameters."),
        ];
    }

    /// <summary>
    /// What opens the stream. Built here rather than in <see cref="StagingStatement"/> because nothing
    /// engine-neutral can render it — <c>FORMAT BINARY</c> is Postgres's own, and this is the one part
    /// of staging that has no equivalent elsewhere.
    /// </summary>
    internal static string BuildCopyCommand(
        SqlDialect dialect, string stagingTable, IReadOnlyList<string> mappedTargetColumns, bool includeChangeOrdering)
    {
        var columns = CopyColumns(mappedTargetColumns, includeChangeOrdering)
            .Select(dialect.QuoteIdentifier);
        return $"COPY {stagingTable} ({string.Join(", ", columns)}) FROM STDIN (FORMAT BINARY)";
    }

    /// <summary>
    /// The columns the stream carries, in the order it writes them: the mapped ones, then phase 132's
    /// two ordering columns when the batch has them, then the operation marker. The ordinal column is
    /// deliberately absent — it is <c>GENERATED ALWAYS AS IDENTITY</c>, so the engine numbers each row
    /// as it lands, exactly as it does for the generic provider's <c>INSERT</c>.
    /// </summary>
    private static IEnumerable<string> CopyColumns(IReadOnlyList<string> mappedTargetColumns, bool includeChangeOrdering)
    {
        foreach (var column in mappedTargetColumns)
            yield return column;
        if (includeChangeOrdering)
        {
            yield return ChangeOrdering.OrderingColumn;
            yield return ChangeOrdering.ChangedAtColumn;
        }
        yield return BatchInsertStagingProvider.OperationColumn;
    }

    private async Task DropAsync(DbConnection connection, string qualifiedTable, CancellationToken cancellationToken)
    {
        using var cmd = connection.CreateTimedCommand();
        // The staging location is a name this provider generated itself, never anything
        // caller-supplied, so interpolating it is safe here in a way it wouldn't be generally.
        cmd.CommandText = dialect.RenderDropTableIfExists(qualifiedTable);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<long> CopyAllAsync(
        NpgsqlConnection connection,
        string stagingTable,
        IReadOnlyList<string> mappedTargetColumns,
        IReadOnlyDictionary<string, string> typeByName,
        IReadOnlyList<ColumnMapping> columnMappings,
        IAsyncEnumerable<ChangeRow> rows,
        bool includeChangeOrdering,
        CancellationToken cancellationToken)
    {
        var copyColumns = CopyColumns(mappedTargetColumns, includeChangeOrdering).ToArray();
        // One resolved NpgsqlDbType per column, computed once rather than per cell. The two ordering
        // columns and the operation marker are this provider's own, so their types come from the same
        // dialect properties StagingStatement.BuildCreate used for their DDL rather than from the
        // target's shape, where they do not appear.
        var copyTypes = new NpgsqlDbType[copyColumns.Length];
        for (var i = 0; i < mappedTargetColumns.Count; i++)
            copyTypes[i] = PostgresNpgsqlTypes.Of(typeByName[mappedTargetColumns[i]]);
        if (includeChangeOrdering)
        {
            copyTypes[mappedTargetColumns.Count] = PostgresNpgsqlTypes.Of(PostgresDialect.Instance.ChangeOrderingColumnType);
            copyTypes[mappedTargetColumns.Count + 1] = PostgresNpgsqlTypes.Of(PostgresDialect.Instance.ChangedAtColumnType);
        }
        copyTypes[^1] = PostgresNpgsqlTypes.Of(PostgresDialect.Instance.OperationMarkerColumnType);

        var sourceColumnByTarget = columnMappings.ToDictionary(m => m.TargetColumn, m => m.SourceColumn);
        var copyCommand = BuildCopyCommand(PostgresDialect.Instance, stagingTable, mappedTargetColumns, includeChangeOrdering);

        long total = 0;
        // Timed the same way every other long-running target operation is: the importer is not a
        // DbCommand, so it would otherwise run on Npgsql's own default rather than on the operator's
        // configured command timeout — the same gap MsSqlStagingTableProvider closed for SqlBulkCopy.
        await using var importer = await connection.BeginBinaryImportAsync(copyCommand, cancellationToken);
        importer.Timeout = TimeSpan.FromSeconds(ConnectionTimeouts.CommandTimeoutOf(connection));

        // Each target column's source ordinal is resolved once, against the first row's schema — the
        // same contract the generic provider relies on, and the reason a change set carries its layout
        // separately from its rows.
        int[]? sourceOrdinalByTarget = null;
        int orderingOrdinal = -1, changedAtOrdinal = -1;

        await foreach (var row in rows.WithCancellation(cancellationToken))
        {
            sourceOrdinalByTarget ??= mappedTargetColumns
                .Select(c => row.Schema.GetOrdinal(sourceColumnByTarget[c]))
                .ToArray();
            if (includeChangeOrdering && orderingOrdinal < 0)
            {
                orderingOrdinal = row.Schema.GetOrdinal(ChangeOrdering.OrderingColumn);
                changedAtOrdinal = row.Schema.GetOrdinal(ChangeOrdering.ChangedAtColumn);
            }

            await importer.StartRowAsync(cancellationToken);
            for (var i = 0; i < mappedTargetColumns.Count; i++)
            {
                await WriteCellAsync(
                    importer, row.Values[sourceOrdinalByTarget[i]], copyTypes[i], copyColumns[i],
                    stagingTable, cancellationToken);
            }
            if (includeChangeOrdering)
            {
                var at = mappedTargetColumns.Count;
                await WriteCellAsync(importer, row.Values[orderingOrdinal], copyTypes[at], copyColumns[at], stagingTable, cancellationToken);
                await WriteCellAsync(importer, row.Values[changedAtOrdinal], copyTypes[at + 1], copyColumns[at + 1], stagingTable, cancellationToken);
            }
            await WriteCellAsync(
                importer, OperationCode(row.Operation), copyTypes[^1], copyColumns[^1], stagingTable, cancellationToken);

            total++;
        }

        await importer.CompleteAsync(cancellationToken);
        return total;
    }

    /// <summary>
    /// One cell, typed to the column it is going into.
    /// <para>
    /// The runtime-type switch is not an optimisation, it is what makes the write work at all: a
    /// staged value arrives boxed as <see cref="object"/> (every reader in this codebase goes through
    /// <c>reader.GetValue(i)</c>), and handing Npgsql a statically-<see cref="object"/> value leaves it
    /// resolving a converter for <c>object</c> rather than for what the value actually is. Naming the
    /// CLR type at the call site is what lets it pick the converter from that type to the column's.
    /// Anything not listed still goes through the untyped overload, which works for the types Npgsql
    /// can resolve at runtime and produces the message below for the rest.
    /// </para>
    /// </summary>
    private static async Task WriteCellAsync(
        NpgsqlBinaryImporter importer,
        object? value,
        NpgsqlDbType type,
        string column,
        string stagingTable,
        CancellationToken cancellationToken)
    {
        if (value is null or DBNull)
        {
            await importer.WriteNullAsync(cancellationToken);
            return;
        }

        try
        {
            switch (value)
            {
                case int v: await importer.WriteAsync(v, type, cancellationToken); break;
                case long v: await importer.WriteAsync(v, type, cancellationToken); break;
                case short v: await importer.WriteAsync(v, type, cancellationToken); break;
                case byte v: await importer.WriteAsync(v, type, cancellationToken); break;
                case bool v: await importer.WriteAsync(v, type, cancellationToken); break;
                case decimal v: await importer.WriteAsync(v, type, cancellationToken); break;
                case double v: await importer.WriteAsync(v, type, cancellationToken); break;
                case float v: await importer.WriteAsync(v, type, cancellationToken); break;
                case string v: await importer.WriteAsync(v, type, cancellationToken); break;
                case char v: await importer.WriteAsync(v.ToString(), type, cancellationToken); break;
                case DateTime v: await importer.WriteAsync(v, type, cancellationToken); break;
                case DateTimeOffset v: await importer.WriteAsync(v, type, cancellationToken); break;
                case DateOnly v: await importer.WriteAsync(v, type, cancellationToken); break;
                case TimeOnly v: await importer.WriteAsync(v, type, cancellationToken); break;
                case TimeSpan v: await importer.WriteAsync(v, type, cancellationToken); break;
                case Guid v: await importer.WriteAsync(v, type, cancellationToken); break;
                case byte[] v: await importer.WriteAsync(v, type, cancellationToken); break;
                default: await importer.WriteAsync(value, type, cancellationToken); break;
            }
        }
        catch (Exception ex) when (ex is InvalidCastException or NotSupportedException or ArgumentException or OverflowException or FormatException)
        {
            throw new InvalidOperationException(
                $"Staging through '{PostgresDriverKinds.CopyStaging}' could not write a " +
                $"{value.GetType().Name} into column '{column}' of {stagingTable}, whose Postgres type " +
                $"is {type}. Binary COPY sends values in the column's own wire format and the server " +
                $"converts nothing, so a value it cannot be written as is refused here rather than " +
                $"coerced. {Hint(value, type)}Staging this mapping through " +
                $"'{PostgresDriverKinds.StagingTable}' instead sends parameters the server will " +
                $"convert, at the cost of the bulk path. Npgsql said: {ex.Message}", ex);
        }
    }

    /// <summary>The one mismatch common enough to name, rather than leaving it to be re-derived from
    /// Npgsql's message every time.</summary>
    private static string Hint(object value, NpgsqlDbType type) =>
        (value, type) switch
        {
            (DateTime dt, NpgsqlDbType.TimestampTz) when dt.Kind != DateTimeKind.Utc =>
                "This value is a DateTime with Kind=" + dt.Kind + ", and the column is 'timestamp with " +
                "time zone', which Npgsql will only write from a UTC one — a source that does not say " +
                "which zone its timestamps are in cannot be assumed to mean UTC. Map to 'timestamp " +
                "without time zone', or transform the value where the zone is known. ",
            (DateTime dt, NpgsqlDbType.Timestamp) when dt.Kind == DateTimeKind.Utc =>
                "This value is a UTC DateTime and the column is 'timestamp without time zone', which " +
                "Npgsql will not write from one, because the result would silently drop the fact that " +
                "it was UTC. Map to 'timestamp with time zone' instead. ",
            _ => "",
        };

    private static string OperationCode(ChangeOperation operation) => operation switch
    {
        ChangeOperation.Insert => "I",
        ChangeOperation.Update => "U",
        ChangeOperation.Delete => "D",
        _ => throw new ArgumentOutOfRangeException(nameof(operation), operation, "Unknown change operation."),
    };
}
