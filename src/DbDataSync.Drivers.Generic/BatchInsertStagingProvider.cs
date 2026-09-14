using System.Data.Common;
using DbDataSync.Core.Config;
using DbDataSync.Drivers.Abstractions;
using DbDataSync.Core.Sql;

namespace DbDataSync.Drivers.Generic;

/// <summary>
/// Stages a change set into a real table via batched, parameterised multi-row <c>INSERT</c>s.
/// <para>
/// Deliberately the lowest common denominator: no temp-table syntax, no bulk-load API, no
/// provider-specific loader. Every engine in scope can run it, including ODBC and JDBC which have no
/// bulk path at all. An engine with something faster gets its own prefixed provider — SQL Server's
/// <c>SqlBulkCopy</c> one already exists, and Postgres <c>COPY</c> is a later phase. This is what makes
/// a new driver a dialect, a connection factory and a catalog rather than a pipeline.
/// </para>
/// <para>
/// A **real** table, not a temp one: <c>#temp</c> is SQL Server's spelling and session-scoped
/// semantics that nothing else shares. That makes <see cref="CleanupAsync"/> load-bearing rather than
/// a convenience — a real table outlives the connection that made it.
/// </para>
/// </summary>
public sealed class BatchInsertStagingProvider(SqlDialect dialect, ITableCatalog catalog)
    : IStagingProvider, IStatementPreview
{
    public const string OperationColumn = "__Operation";

    /// <summary>
    /// The per-pass row ordinal a chunked apply ranges over. Filled in by the engine, never by the
    /// source: staging holds whatever rows arrived, in the order they arrived, and that order is the
    /// only thing a writer needs in order to take them a chunk at a time.
    /// <para>
    /// Named like <see cref="OperationColumn"/> and for the same reason — a double-underscore prefix
    /// keeps it out of the way of a real mapped column.
    /// </para>
    /// </summary>
    public const string OrdinalColumn = "__Ordinal";

    public string Kind => GenericDriverKinds.StagingTable;

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

        await dialect.UseDatabaseAsync(targetConnection, target.Database, cancellationToken);

        var mappedTargetColumns = columnMappings.Select(m => m.TargetColumn).Distinct().ToList();
        var typeByName = mappedTargetColumns.ToDictionary(
            c => c, c => targetColumns.RequireColumn(mappingName, "target", c).NativeType,
            StringComparer.OrdinalIgnoreCase);

        // Phase 132: the two ordering columns cannot be known from the target's own schema — they are
        // reader-only pass-through columns, present only when the reader stated them — so the only way
        // to know whether to add them to this table's DDL is to look at the first staged row itself.
        // The staging table is created before any row is read below, so that first row has to be
        // peeked and replayed rather than merely inspected.
        var (hasChangeOrdering, effectiveRows) = await ChangeOrdering.DetectAsync(rows, cancellationToken);

        // In the target's own schema, because there is no portable scratch namespace. The GUID is what
        // keeps concurrent runs — and concurrent segments of one run — from colliding.
        var stagingTable = dialect.QualifyTable(target.Schema, $"DS_STG_{Guid.NewGuid():N}");

        using (var createCmd = targetConnection.CreateTimedCommand())
        {
            createCmd.CommandText = StagingStatement.BuildCreate(dialect, stagingTable, mappedTargetColumns, typeByName, hasChangeOrdering);
            await createCmd.ExecuteNonQueryAsync(cancellationToken);
        }

        try
        {
            var rowCount = await InsertAllAsync(
                targetConnection, stagingTable, mappedTargetColumns, columnMappings, effectiveRows, hasChangeOrdering, cancellationToken);
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

    /// <inheritdoc cref="MsSqlStagingTableProvider.DescribeAsync"/>
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
                StagingStatement.BuildInsert(dialect, stagingTable, mapped, rowCount: 1), PreviewOrigin.BuiltIn,
                $"One row shown; a pass batches up to {StagingStatement.RowsPerStatement(dialect, mapped.Count + 1)} " +
                "rows per statement, bounded by the dialect's parameter limit."),
        ];
    }

    public async Task CleanupAsync(
        DbConnection targetConnection, StagedChangeSet staged, CancellationToken cancellationToken) =>
        await DropAsync(targetConnection, staged.StagingLocation, cancellationToken);

    private async Task DropAsync(DbConnection connection, string qualifiedTable, CancellationToken cancellationToken)
    {
        using var cmd = connection.CreateTimedCommand();
        // The staging location is a name this provider generated itself, never anything
        // caller-supplied, so interpolating it is safe here in a way it wouldn't be generally.
        cmd.CommandText = dialect.RenderDropTableIfExists(qualifiedTable);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<long> InsertAllAsync(
        DbConnection connection,
        string stagingTable,
        IReadOnlyList<string> mappedTargetColumns,
        IReadOnlyList<ColumnMapping> columnMappings,
        IAsyncEnumerable<ChangeRow> rows,
        bool includeChangeOrdering,
        CancellationToken cancellationToken)
    {
        var sourceColumnByTarget = columnMappings.ToDictionary(m => m.TargetColumn, m => m.SourceColumn);
        // +1 for the operation marker, which is bound like any other value; +2 more for the ordering
        // columns (phase 132) when the staged batch carries them.
        var valuesPerRow = mappedTargetColumns.Count + (includeChangeOrdering ? 2 : 0) + 1;
        var rowsPerStatement = StagingStatement.RowsPerStatement(dialect, valuesPerRow);

        var batch = new List<object?[]>(rowsPerStatement);
        long total = 0;
        // Each target column's source ordinal is resolved once, against the first row's schema — the
        // same contract ChangeRowDataReader relies on, and the reason a change set carries its layout
        // separately from its rows. Looking columns up by name per cell would put a hash lookup back
        // in the hot path this design exists to keep out of it.
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

            var values = new object?[valuesPerRow];
            for (var i = 0; i < mappedTargetColumns.Count; i++)
                values[i] = row.Values[sourceOrdinalByTarget[i]];
            if (includeChangeOrdering)
            {
                values[mappedTargetColumns.Count] = row.Values[orderingOrdinal];
                values[mappedTargetColumns.Count + 1] = row.Values[changedAtOrdinal];
            }
            values[^1] = OperationCode(row.Operation);
            batch.Add(values);

            if (batch.Count == rowsPerStatement)
            {
                total += await FlushAsync(connection, stagingTable, mappedTargetColumns, batch, includeChangeOrdering, cancellationToken);
                batch.Clear();
            }
        }

        if (batch.Count > 0)
            total += await FlushAsync(connection, stagingTable, mappedTargetColumns, batch, includeChangeOrdering, cancellationToken);

        return total;
    }

    private async Task<int> FlushAsync(
        DbConnection connection,
        string stagingTable,
        IReadOnlyList<string> mappedTargetColumns,
        IReadOnlyList<object?[]> batch,
        bool includeChangeOrdering,
        CancellationToken cancellationToken)
    {
        using var cmd = connection.CreateTimedCommand();
        cmd.CommandText = StagingStatement.BuildInsert(dialect, stagingTable, mappedTargetColumns, batch.Count, includeChangeOrdering);

        for (var r = 0; r < batch.Count; r++)
        {
            for (var v = 0; v < batch[r].Length; v++)
                cmd.AddParameter(dialect.ParameterName(StagingStatement.ParameterName(r, v)), batch[r][v]);
        }

        await cmd.ExecuteNonQueryAsync(cancellationToken);
        return batch.Count;
    }

    private static string OperationCode(ChangeOperation operation) => operation switch
    {
        ChangeOperation.Insert => "I",
        ChangeOperation.Update => "U",
        ChangeOperation.Delete => "D",
        _ => throw new ArgumentOutOfRangeException(nameof(operation), operation, "Unknown change operation."),
    };
}

/// <summary>Statement text and batching arithmetic for <see cref="BatchInsertStagingProvider"/>,
/// separated so both can be asserted without a live server.</summary>
public static class StagingStatement
{
    public static string ParameterName(int rowIndex, int valueIndex) => $"__s{rowIndex}_{valueIndex}";

    /// <summary>
    /// How many rows one statement may carry. Derived from the column count against the dialect's
    /// limit, never fixed: a fixed batch size is exactly the defect that made 500 rows of a 5-column
    /// table overflow SQL Server's 2100-parameter cap. One parameter of headroom is left over for
    /// anything a dialect's rendering needs to bind alongside the values.
    /// </summary>
    public static int RowsPerStatement(SqlDialect dialect, int valuesPerRow) =>
        Math.Max(1, (dialect.MaxParametersPerStatement - 1) / valuesPerRow);

    public static string BuildCreate(
        SqlDialect dialect, string qualifiedTable, IReadOnlyList<string> columns, IReadOnlyDictionary<string, string> typeByName,
        bool includeChangeOrdering = false)
    {
        // Every staged column is nullable regardless of the target's constraint: staging holds what the
        // source produced, and a deleted row carries only its key. Constraints are the target's to
        // enforce when the writer applies the change set, not staging's to re-impose on the way in.
        var defs = columns.Select(c => $"{dialect.QuoteIdentifier(c)} {typeByName[c]} NULL").ToList();
        // Phase 132: nullable for the same reason as every other staged column.
        if (includeChangeOrdering)
        {
            defs.Add($"{dialect.QuoteIdentifier(ChangeOrdering.OrderingColumn)} {dialect.ChangeOrderingColumnType} NULL");
            defs.Add($"{dialect.QuoteIdentifier(ChangeOrdering.ChangedAtColumn)} {dialect.ChangedAtColumnType} NULL");
        }
        return $"CREATE TABLE {qualifiedTable} ({string.Join(", ", defs)}, " +
               $"{dialect.QuoteIdentifier(BatchInsertStagingProvider.OperationColumn)} {dialect.OperationMarkerColumnType} NOT NULL, " +
               $"{dialect.RenderStagingOrdinalColumn(BatchInsertStagingProvider.OrdinalColumn)});";
    }

    public static string BuildInsert(
        SqlDialect dialect, string qualifiedTable, IReadOnlyList<string> columns, int rowCount,
        bool includeChangeOrdering = false)
    {
        var allColumns = new List<string>(columns);
        if (includeChangeOrdering)
        {
            allColumns.Add(ChangeOrdering.OrderingColumn);
            allColumns.Add(ChangeOrdering.ChangedAtColumn);
        }

        var columnList = string.Join(", ",
            allColumns.Select(dialect.QuoteIdentifier)
                .Append(dialect.QuoteIdentifier(BatchInsertStagingProvider.OperationColumn)));

        var valuesPerRow = allColumns.Count + 1;
        var tuples = Enumerable.Range(0, rowCount)
            .Select(r => "(" + string.Join(", ",
                Enumerable.Range(0, valuesPerRow).Select(v => dialect.ParameterReference(ParameterName(r, v)))) + ")")
            .ToList();

        return dialect.RenderMultiRowInsert(qualifiedTable, columnList, tuples);
    }
}
