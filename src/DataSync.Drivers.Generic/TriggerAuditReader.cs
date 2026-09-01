using System.Data.Common;
using System.Runtime.CompilerServices;
using DataSync.Core.Config;
using DataSync.Drivers.Abstractions;
using DataSync.Core.Sql;

namespace DataSync.Drivers.Generic;

/// <summary>
/// Reads changes from a trigger-maintained shadow table.
///
/// <para>
/// **One reader for every engine that has triggers.** The read side is ordinary SQL — select from a
/// shadow table above a sequence, collapse to the net change per key, join back for current values —
/// so only quoting and placeholders come from the <see cref="SqlDialect"/>. That is what makes this
/// the cheapest large win in the change-tracking set: it delivers delete detection to SQL Server,
/// Postgres, and anything reachable through ODBC or JDBC at once, where every log-based mechanism is
/// one engine's and needs its own provider, privilege and server setting.
/// </para>
///
/// <para>
/// The *setup* side is not generic and does not pretend to be: <c>CREATE TRIGGER</c> diverges more
/// than almost anything else in SQL, so the DDL is a per-engine provisioning step that an operator
/// previews and applies.
/// </para>
///
/// <para>
/// **A trigger costs every transaction that touches the table, forever.** That is the trade this
/// mechanism makes, and the UI says so where an operator turns it on rather than leaving them to find
/// out from a latency graph.
/// </para>
/// </summary>
public sealed class TriggerAuditReader(SqlDialect dialect, ITableCatalog catalog)
    : IChangeReader, IStatementPreview, IPositionAcknowledging
{
    /// <summary>
    /// Deletes shadow rows at or below an acknowledged position, once the pass that read them has
    /// written and stored its watermark. Off by default: pruning is a delete against somebody's source
    /// database, and doing it unasked is not this tool's call to make.
    /// </summary>
    public const string PruneOption = "pruneAcknowledged";

    public string Kind => GenericDriverKinds.TriggerAudit;

    /// <summary>The shadow row records a delete with its key, which is the whole reason to take on a
    /// trigger's cost rather than scanning a watermark column.</summary>
    public bool DetectsDeletes => true;

    public IReadOnlyList<ParameterDescriptor> Parameters { get; } =
    [
        new()
        {
            Name = PruneOption,
            Label = "Prune applied changes",
            Description =
                "Deletes shadow-table rows once the pass that read them has written and recorded its " +
                "position. Without it the shadow table grows for as long as the replication runs.",
            Type = ParameterType.Bool,
            Default = "false",
        },
    ];

    public async Task<ReadResult> ReadChangesAsync(
        DbConnection sourceConnection,
        SourceTableRef source,
        string? previousWatermark,
        IReadOnlyList<ColumnMapping> columnMappings,
        IReadOnlyDictionary<string, string> options,
        CancellationToken cancellationToken)
    {
        await dialect.UseDatabaseAsync(sourceConnection, source.Database, cancellationToken);

        var target = await GetMaxSequenceAsync(sourceConnection, source, cancellationToken);
        var diagnostics = new ReadDiagnostics();

        if (previousWatermark is null)
        {
            // The shadow table only holds what has happened since the trigger was created, so a table
            // that already had rows would otherwise start half-replicated with nothing to say so —
            // the same first-pass rule every change-feed reader here follows.
            var rows = ReadFullLoadAsync(
                sourceConnection, source, SourceProjection.Render(dialect, columnMappings), cancellationToken);
            return new ReadResult(rows, target.ToString(), diagnostics);
        }

        var previous = long.Parse(previousWatermark);
        if (previous >= target)
            return new ReadResult(Empty(), previousWatermark, diagnostics);

        return new ReadResult(
            ReadIncrementalAsync(sourceConnection, source, previous, target, columnMappings, diagnostics, cancellationToken),
            target.ToString(),
            diagnostics);
    }

    public async Task AcknowledgeAsync(
        DbConnection sourceConnection,
        SourceTableRef source,
        string watermark,
        IReadOnlyDictionary<string, string> options,
        CancellationToken cancellationToken)
    {
        if (!Prunes(options))
            return;

        await dialect.UseDatabaseAsync(sourceConnection, source.Database, cancellationToken);

        using var cmd = sourceConnection.CreateTimedCommand();
        cmd.CommandText = TriggerAuditStatement.BuildPrune(dialect, source.Schema, source.Table);
        cmd.AddParameter(dialect.ParameterName("throughSequence"), long.Parse(watermark));
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<PreviewStatement>> DescribeAsync(
        PreviewRequest request, CancellationToken cancellationToken)
    {
        await dialect.UseDatabaseAsync(request.Connection, request.Source.Database, cancellationToken);

        if (request.PreviousWatermark is null)
        {
            return
            [
                new PreviewStatement(
                    PreviewStages.SourceRead,
                    "Full load — no shadow-table position stored yet, so the next pass reads every row",
                    SourceProjection.Render(dialect, request.ColumnMappings) is var projection
                        ? $"SELECT {projection} FROM {dialect.QualifyTable(request.Source.Schema, request.Source.Table)};"
                        : null,
                    PreviewOrigin.BuiltIn),
            ];
        }

        var (keys, nonKeys) = await ResolveColumnsAsync(request.Connection, request.Source, cancellationToken);

        // The sequence ReadIncrementalAsync would fix as its own upper bound, fetched here for the
        // same reason MsSqlChangeTrackingReader fetches its current version: a preview showing
        // @targetSequence as a bare placeholder gives an admin nothing a query tool can resolve.
        var target = await GetMaxSequenceAsync(request.Connection, request.Source, cancellationToken);
        List<PreviewParameter> parameters =
        [
            new("previousSequence", "bigint", request.PreviousWatermark!),
            new("targetSequence", "bigint", target.ToString()),
        ];

        return
        [
            new PreviewStatement(
                PreviewStages.SourceRead,
                $"Incremental read of changes after sequence {request.PreviousWatermark}",
                TriggerAuditStatement.BuildRead(
                    dialect, request.Source.Schema, request.Source.Table, keys, nonKeys,
                    column => RenderNonKeyColumn(column, request.ColumnMappings)),
                PreviewOrigin.BuiltIn,
                Prunes(request.Options)
                    ? "Applied rows are deleted from the shadow table once the pass has written and " +
                      "recorded its position."
                    : "The shadow table is not pruned, so it grows for as long as this replication runs.",
                dialect.RenderDeclarations(parameters)),
        ];
    }

    private static bool Prunes(IReadOnlyDictionary<string, string> options) =>
        options.TryGetValue(PruneOption, out var raw)
        && (string.Equals(raw, "true", StringComparison.OrdinalIgnoreCase) || raw == "1");

    private async Task<(IReadOnlyList<string> Keys, IReadOnlyList<string> NonKeys)> ResolveColumnsAsync(
        DbConnection connection, SourceTableRef source, CancellationToken cancellationToken)
    {
        var columns = await catalog.GetColumnsAsync(connection, source.Schema, source.Table, cancellationToken);
        var keys = columns.Where(c => c.IsPrimaryKey).Select(c => c.Name).ToList();

        if (keys.Count == 0)
            throw new InvalidOperationException(
                $"Table '{source.Schema}.{source.Table}' has no primary key. A trigger-audit shadow " +
                "table is keyed by it — without one there is nothing to collapse changes by and " +
                "nothing to identify a deleted row with.");

        return (keys, columns.Where(c => !c.IsPrimaryKey).Select(c => c.Name).ToList());
    }

    private async Task<long> GetMaxSequenceAsync(
        DbConnection connection, SourceTableRef source, CancellationToken cancellationToken)
    {
        using var cmd = connection.CreateTimedCommand();
        cmd.CommandText = TriggerAuditStatement.BuildMaxSequence(dialect, source.Schema, source.Table);

        try
        {
            var result = await cmd.ExecuteScalarAsync(cancellationToken);
            // Null is an empty shadow table, which is a perfectly ordinary state — nothing has changed
            // since the trigger was created.
            return result is null or DBNull ? 0 : Convert.ToInt64(result);
        }
        catch (DbException ex)
        {
            throw new InvalidOperationException(
                $"Could not read the shadow table for '{source.Schema}.{source.Table}'. Change capture " +
                "has to be enabled for this table first — the Setup card generates the trigger and the " +
                $"shadow table for this engine. ({ex.Message})", ex);
        }
    }

    private static async IAsyncEnumerable<ChangeRow> Empty()
    {
        await Task.CompletedTask;
        yield break;
    }

    private async IAsyncEnumerable<ChangeRow> ReadFullLoadAsync(
        DbConnection connection, SourceTableRef source, string projection,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var filter = string.IsNullOrWhiteSpace(source.Filter) ? "" : $" WHERE {source.Filter}";

        using var cmd = connection.CreateTimedCommand();
        cmd.CommandText =
            $"SELECT {projection} FROM {dialect.QualifyTable(source.Schema, source.Table)}{filter};";

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        var schema = ResultSetSchema.From(reader);
        while (await reader.ReadAsync(cancellationToken))
            yield return new ChangeRow(ChangeOperation.Insert, schema, ResultSetSchema.ReadValues(reader, schema.Count));
    }

    private async IAsyncEnumerable<ChangeRow> ReadIncrementalAsync(
        DbConnection connection,
        SourceTableRef source,
        long previous,
        long target,
        IReadOnlyList<ColumnMapping> columnMappings,
        ReadDiagnostics diagnostics,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var (keys, nonKeys) = await ResolveColumnsAsync(connection, source, cancellationToken);
        var schema = new ChangeSchema([.. keys, .. nonKeys]);

        using var cmd = connection.CreateTimedCommand();
        cmd.CommandText = TriggerAuditStatement.BuildRead(
            dialect, source.Schema, source.Table, keys, nonKeys,
            column => RenderNonKeyColumn(column, columnMappings));
        cmd.AddParameter(dialect.ParameterName("previousSequence"), previous);
        cmd.AddParameter(dialect.ParameterName("targetSequence"), target);

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var operation = reader.GetString(TriggerAuditStatement.OperationOrdinal) switch
            {
                "I" => ChangeOperation.Insert,
                "U" => ChangeOperation.Update,
                "D" => ChangeOperation.Delete,
                var op => throw new InvalidOperationException(
                    $"The shadow table for '{source.Schema}.{source.Table}' holds an unknown operation " +
                    $"'{op}'. The trigger that wrote it is not the one this reader generates."),
            };

            var sourceRowGone = Convert.ToInt32(reader.GetValue(TriggerAuditStatement.BaseMissingOrdinal)) == 1;

            // An insert or update whose row has since been deleted carries no values — every non-key
            // column is a LEFT JOIN NULL, indistinguishable from real data. Skipped rather than
            // applied as nonsense: the delete that removed it is a later shadow row, above this pass's
            // target sequence, so it arrives on the next pass. Same reasoning, and the same counter,
            // as the Change Tracking reader.
            if (sourceRowGone && operation != ChangeOperation.Delete)
            {
                diagnostics.RowsSkippedSourceRowGone++;
                continue;
            }

            // A delete's row is gone from the base table, so only the key columns are reliable — the
            // rest stays unset rather than being filled with the LEFT JOIN's NULLs.
            var populated = operation == ChangeOperation.Delete ? keys.Count : schema.Count;
            var values = new object?[schema.Count];
            for (var i = 0; i < populated; i++)
            {
                var ordinal = i + TriggerAuditStatement.FirstKeyOrdinal;
                values[i] = reader.IsDBNull(ordinal) ? null : reader.GetValue(ordinal);
            }

            yield return new ChangeRow(operation, schema, values);
        }
    }

    /// <summary>
    /// A non-key column, with its transform if it has one.
    /// <para>
    /// The read joins the base table under the alias <c>base</c>, so <c>{{column}}</c> has to resolve
    /// to <c>base.&lt;col&gt;</c> and not to a bare name — which for a key column would be ambiguous
    /// against the shadow table's own copy. That is the reason the token exists at all; see the same
    /// method on the Change Tracking reader.
    /// </para>
    /// </summary>
    private string RenderNonKeyColumn(string column, IReadOnlyList<ColumnMapping> columnMappings)
    {
        var baseReference = $"base.{dialect.QuoteIdentifier(column)}";
        var mapping = columnMappings.FirstOrDefault(
            m => string.Equals(m.SourceColumn, column, StringComparison.OrdinalIgnoreCase)
                 && !string.IsNullOrWhiteSpace(m.Transform));

        if (mapping is null)
            return baseReference;

        var expression = mapping.Transform!.Contains(ColumnMapping.ColumnToken, StringComparison.Ordinal)
            ? mapping.Transform.Replace(ColumnMapping.ColumnToken, baseReference, StringComparison.Ordinal)
            : mapping.Transform;

        return $"{expression} AS {dialect.QuoteIdentifier(column)}";
    }
}
