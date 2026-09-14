using System.Data.Common;
using DbDataSync.Core.Config;
using DbDataSync.Drivers.Abstractions;
using Microsoft.Data.SqlClient;
using DbDataSync.Core.Sql;

namespace DbDataSync.Drivers.MsSql;

/// <summary>
/// Stages a change set into a session-scoped local temp table (<c>#Staging_...</c>) via
/// <see cref="SqlBulkCopy"/>, populated column-by-column according to <see cref="ColumnMapping"/>
/// plus a trailing "__Operation" marker column consumed by <see cref="MsSqlMergeWriter"/>.
/// <para>
/// Local temp tables are scoped to the connection/session, so the caller must pass the *same* open
/// <see cref="DbConnection"/> to <see cref="MsSqlMergeWriter.ApplyAsync"/> that was used here — the
/// staging table would otherwise not exist. This mirrors how DbDataSync.TaskRunner is expected to use
/// one connection per target throughout a run (Phase 4).
/// </para>
/// </summary>
public sealed class MsSqlStagingTableProvider : IStagingProvider, IStatementPreview
{
    private const string OperationColumn = "__Operation";

    /// <summary>Taken from the writer's own constant rather than restated: the chunked apply ranges
    /// over this column, so the two disagreeing about its name would be a runtime failure.</summary>
    private const string OrdinalColumn = MsSqlTargetShape.OrdinalColumn;

    public string Kind => MsSqlDriverKinds.StagingTable;

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

        targetConnection.ChangeDatabase(target.Database);

        var targetColumnTypes = await MsSqlSchemaQueries.GetColumnsAsync(targetConnection, target.Schema, target.Table, cancellationToken);
        var typeByName = targetColumnTypes.ToDictionary(c => c.Name, c => c.NativeType, StringComparer.OrdinalIgnoreCase);

        var mappedTargetColumns = columnMappings.Select(m => m.TargetColumn).Distinct().ToList();
        foreach (var column in mappedTargetColumns)
        {
            if (!typeByName.ContainsKey(column))
                throw new InvalidOperationException(
                    $"Target column '{column}' was not found on '{target.Schema}.{target.Table}'.");
        }

        // Phase 132: the two ordering columns cannot be known from the target's own schema — they are
        // reader-only pass-through columns, present only when the reader stated them — so the only way
        // to know whether to add them to this table's DDL is to look at the first staged row itself.
        // The staging table is created before any row is read (bulk copy has not started yet), so that
        // first row has to be peeked and replayed rather than merely inspected.
        var (hasChangeOrdering, effectiveRows) = await ChangeOrdering.DetectAsync(rows, cancellationToken);

        var stagingTable = $"#Staging_{Guid.NewGuid():N}";

        using (var createCmd = targetConnection.CreateTimedCommand())
        {
            createCmd.CommandText = BuildCreateStagingTable(stagingTable, mappedTargetColumns, typeByName, hasChangeOrdering);
            await createCmd.ExecuteNonQueryAsync(cancellationToken);
        }

        var rowCount = await BulkCopyAsync(
            targetConnection, stagingTable, mappedTargetColumns, columnMappings, effectiveRows, hasChangeOrdering, cancellationToken);

        return new StagedChangeSet(stagingTable, rowCount, hasChangeOrdering);
    }

    /// <summary>
    /// The staging table this pass would create, with the target's own column types — which is the
    /// part worth previewing, because a mapped column the target does not have fails here rather than
    /// at the write, and the DDL is where that becomes visible.
    /// </summary>
    public async Task<IReadOnlyList<PreviewStatement>> DescribeAsync(
        PreviewRequest request, CancellationToken cancellationToken)
    {
        request.Connection.ChangeDatabase(request.Target.Database);

        var columns = await MsSqlSchemaQueries.GetColumnsAsync(
            request.Connection, request.Target.Schema, request.Target.Table, cancellationToken);
        var typeByName = columns.ToDictionary(c => c.Name, c => c.NativeType, StringComparer.OrdinalIgnoreCase);
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

        // A fresh name per pass, so the one shown is illustrative rather than the one that will exist.
        var stagingTable = "#Staging_<per pass>";
        return
        [
            new PreviewStatement(
                PreviewStages.Staging, "Create the staging table",
                BuildCreateStagingTable(stagingTable, mapped, typeByName), PreviewOrigin.BuiltIn,
                "A local temporary table, dropped when the pass finishes with it."),

            new PreviewStatement(
                PreviewStages.Staging, "Load the rows into it", null, PreviewOrigin.BuiltIn,
                $"SqlBulkCopy into {stagingTable} — a bulk stream rather than a statement, which is " +
                "why there is none to show."),
        ];
    }

    /// <summary>One builder for the DDL, so the preview and the run cannot disagree about it.</summary>
    private static string BuildCreateStagingTable(
        string stagingTable, IReadOnlyList<string> columns, IReadOnlyDictionary<string, string> typeByName,
        bool includeChangeOrdering = false)
    {
        var columnDefs = string.Join(", ", columns.Select(c => $"{SqlIdentifier.Quote(c)} {typeByName[c]} NULL"));
        // Phase 132: nullable, like every staged column — a delete's non-key values are already null by
        // the reader's own convention, and these two are no different.
        var orderingDefs = includeChangeOrdering
            ? $"{SqlIdentifier.Quote(ChangeOrdering.OrderingColumn)} {MsSqlDialect.Instance.ChangeOrderingColumnType} NULL, " +
              $"{SqlIdentifier.Quote(ChangeOrdering.ChangedAtColumn)} {MsSqlDialect.Instance.ChangedAtColumnType} NULL, "
            : "";
        // The ordinal is what MsSqlMergeWriter's chunked apply ranges over, and SqlBulkCopy fills it
        // in for free: the column is not in ColumnMappings, and KeepIdentity is off, so the engine
        // numbers each row as it lands.
        return $"CREATE TABLE {stagingTable} ({columnDefs}, {orderingDefs}{OperationColumn} CHAR(1) NOT NULL, " +
               $"{MsSqlDialect.Instance.RenderStagingOrdinalColumn(OrdinalColumn)});";
    }

    public async Task CleanupAsync(
        DbConnection targetConnection, StagedChangeSet staged, CancellationToken cancellationToken)
    {
        using var cmd = targetConnection.CreateTimedCommand();
        // The staging location is a name this provider generated itself (#Staging_{guid:N}), never
        // anything caller-supplied, so interpolating it is safe here in a way it wouldn't be generally.
        cmd.CommandText = $"DROP TABLE IF EXISTS {staged.StagingLocation};";
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<long> BulkCopyAsync(
        DbConnection targetConnection,
        string stagingTable,
        IReadOnlyList<string> mappedTargetColumns,
        IReadOnlyList<ColumnMapping> columnMappings,
        IAsyncEnumerable<ChangeRow> rows,
        bool includeChangeOrdering,
        CancellationToken cancellationToken)
    {
        var sourceColumnByTarget = columnMappings.ToDictionary(m => m.TargetColumn, m => m.SourceColumn);

        // Phase 132: the two ordering columns are self-mapped — same name in the reader's schema as in
        // the staging table — so ChangeRowDataReader needs no change at all; it already resolves each
        // "target" column's ordinal by looking up sourceColumnByTarget[targetColumn] against the row's
        // schema, and an identity entry does exactly that.
        var readerColumns = mappedTargetColumns;
        if (includeChangeOrdering)
        {
            readerColumns = [.. mappedTargetColumns, ChangeOrdering.OrderingColumn, ChangeOrdering.ChangedAtColumn];
            sourceColumnByTarget = new Dictionary<string, string>(sourceColumnByTarget)
            {
                [ChangeOrdering.OrderingColumn] = ChangeOrdering.OrderingColumn,
                [ChangeOrdering.ChangedAtColumn] = ChangeOrdering.ChangedAtColumn,
            };
        }

        using var bulkCopy = new SqlBulkCopy((SqlConnection)targetConnection)
        {
            DestinationTableName = stagingTable,
            // SqlBulkCopy is not a DbCommand, so it never went through CreateTimedCommand and quietly
            // ran on its own 30-second default regardless of the operator's configured command
            // timeout — the same class of long-running target operation as a chunked MERGE, timed out
            // by a completely different number. Reusing the connection's own stamp keeps one dial for
            // "how long may work against this target run" rather than adding a second one.
            BulkCopyTimeout = ConnectionTimeouts.CommandTimeoutOf(targetConnection),
        };
        foreach (var column in readerColumns)
            bulkCopy.ColumnMappings.Add(column, column);
        bulkCopy.ColumnMappings.Add(OperationColumn, OperationColumn);

        await using var dataReader = new ChangeRowDataReader(rows, readerColumns, sourceColumnByTarget, cancellationToken);
        await bulkCopy.WriteToServerAsync(dataReader, cancellationToken);
        return dataReader.RowsProduced;
    }
}
