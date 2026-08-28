using System.Data.Common;
using System.Runtime.CompilerServices;
using DataSync.Core.Config;
using DataSync.Drivers.Abstractions;
using DataSync.Drivers.Generic;
using DataSync.Scripting.Abstractions;

namespace DataSync.Scripting;

/// <summary>
/// A reader whose statement comes from a script.
/// <para>
/// Most change-tracking mechanisms are, at read time, a <c>SELECT</c> returning rows with an operation
/// and a position — SQL Server's <c>CHANGETABLE</c> and <c>fn_cdc_get_all_changes</c>, Postgres's
/// <c>pg_logical_slot_peek_changes</c>, Oracle's <c>V$LOGMNR_CONTENTS</c> and Flashback, and any
/// hand-rolled audit table that predates this tool. This is how DataSync consumes one it has no driver
/// for, with the SQL supplied by the operator instead of by us.
/// </para>
/// <para>
/// A **Kind**, not a hook inside the existing readers. An operator picks it in the pipeline's reader
/// picker and nothing else is silently reshaped — the lesson phase 23 learned when the column-expression
/// hook moved out of <c>SourceProjection</c>.
/// </para>
/// </summary>
public sealed class ScriptedQueryReader(ScriptHost scriptHost, SqlDialect dialect, string engineName, ITableCatalog catalog)
    : IChangeReader
{
    /// <summary>The reader option naming the script. A pipeline-stage choice, so it is bound the way
    /// every other stage option is rather than through the script hierarchy — the Kind and its script
    /// are one decision and belong in one place.</summary>
    public const string ScriptOption = "script";

    public string Kind => "ScriptedQuery";

    /// <summary>
    /// **False, even though a scripted query is the reader most likely to surface deletes.**
    /// <para>
    /// Capabilities are per *Kind*, and this Kind is one reader shared by every script bound to it — so
    /// there is no honest per-script answer to give here. Phase 17's rule decides which way to be wrong:
    /// false because overstating the guarantee is what loses data. A reader that claims deletes and does
    /// not surface them leaves a target quietly accumulating rows the source removed, and an operator
    /// who has been told deletes are covered has no reason to look. The opposite error is merely
    /// pessimistic — a reload nobody needed.
    /// </para>
    /// </summary>
    public bool DetectsDeletes => false;

    public async Task<ReadResult> ReadChangesAsync(
        DbConnection sourceConnection,
        SourceTableRef source,
        string? previousWatermark,
        IReadOnlyList<ColumnMapping> columnMappings,
        IReadOnlyDictionary<string, string> options,
        CancellationToken cancellationToken)
    {
        if (!options.TryGetValue(ScriptOption, out var scriptName) || string.IsNullOrWhiteSpace(scriptName))
            throw new ScriptExecutionException(
                $"The '{Kind}' reader needs a '{ScriptOption}' option naming the script that builds its query.");

        var builder = scriptHost.Resolve<ISourceQueryBuilder>(scriptName);

        await dialect.UseDatabaseAsync(sourceConnection, source.Database, cancellationToken);
        var columns = await catalog.GetColumnsAsync(sourceConnection, source.Schema, source.Table, cancellationToken);

        var context = new SourceQueryContext(
            source, columnMappings, columns, previousWatermark, EndWatermark: null,
            SegmentSerializer.ReadOptional(options),
            new ScriptDialectAdapter(dialect, engineName),
            new ScriptParameters(options));

        // The window's end is fixed before the read, so the result cannot grow while it is consumed —
        // the bounded-window shape MsSqlChangeTrackingReader already uses and every log-based reader
        // will need (see planning/todo/change-tracking-strategies.md).
        var endWatermark = await ReadEndWatermarkAsync(
            sourceConnection, builder, context, scriptName, cancellationToken);

        var readContext = context with { EndWatermark = endWatermark };
        var shape = Guarded(() => builder.DescribeResult(readContext), scriptName, "describing its result");
        var query = Guarded(() => builder.BuildReadQuery(readContext), scriptName, "building its read query");

        // Echoed back rather than invented when the mechanism has no position of its own — the same
        // thing BatchReloadReader does, so a standalone reload leaves the stored watermark as it found
        // it instead of writing a meaningless one over it.
        return new ReadResult(
            ReadRowsAsync(sourceConnection, query, shape, cancellationToken),
            endWatermark ?? previousWatermark ?? "");
    }

    private async Task<string?> ReadEndWatermarkAsync(
        DbConnection connection,
        ISourceQueryBuilder builder,
        SourceQueryContext context,
        string scriptName,
        CancellationToken cancellationToken)
    {
        var query = Guarded(() => builder.BuildWatermarkQuery(context), scriptName, "building its watermark query");
        if (query is null)
            return null;

        using var cmd = connection.CreateCommand();
        Bind(cmd, query);
        var value = await cmd.ExecuteScalarAsync(cancellationToken);
        return value is null or DBNull ? null : WatermarkValue.Format(value);
    }

    private async IAsyncEnumerable<ChangeRow> ReadRowsAsync(
        DbConnection connection,
        SourceQuery query,
        SourceQueryShape shape,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var cmd = connection.CreateCommand();
        Bind(cmd, query);

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        var resultSchema = ResultSetSchema.From(reader);
        var plan = ScriptedQueryResultPlan.Build(resultSchema, shape);

        while (await reader.ReadAsync(cancellationToken))
        {
            var values = ResultSetSchema.ReadValues(reader, resultSchema.Count);
            yield return new ChangeRow(plan.OperationOf(values), plan.Schema, plan.Project(values));
        }
    }

    private void Bind(DbCommand command, SourceQuery query)
    {
        command.CommandText = query.CommandText;
        foreach (var parameter in query.Parameters)
            command.AddParameter(dialect.ParameterName(parameter.Name), parameter.Value);
    }

    private static T Guarded<T>(Func<T> action, string scriptName, string what)
    {
        try
        {
            return action();
        }
        catch (Exception ex) when (ex is not ScriptExecutionException)
        {
            throw new ScriptExecutionException($"Script '{scriptName}' threw while {what}: {ex.Message}", ex);
        }
    }
}
