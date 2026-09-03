using System.Runtime.CompilerServices;
using DbDataSync.Core.Config;
using DbDataSync.Drivers.Abstractions;
using DbDataSync.Scripting.Abstractions;

namespace DbDataSync.Scripting;

/// <summary>
/// Wraps the reader's row stream with the in-process transforms, between reading and staging.
/// <para>
/// Order is source SQL (already applied by the time a row arrives), then value expressions, then the
/// row transform — forced by where each one lives.
/// </para>
/// </summary>
public sealed class TransformPipeline
{
    /// <summary>A column the value expression asked for: where it sits in the reader's result, and the
    /// context handed to the script for it. Paired so the hot path reaches both in one step.</summary>
    private readonly record struct ValueTarget(int Ordinal, ValueColumnExpressionContext Context);

    private readonly IValueColumnExpression? _values;
    private readonly IRowTransform? _row;
    private readonly RowTransformContext? _rowContext;
    private readonly IReadOnlyList<ValueColumnExpressionContext> _declared;

    private TransformPipeline(
        IValueColumnExpression? values,
        IReadOnlyList<ValueColumnExpressionContext> declared,
        IRowTransform? row,
        RowTransformContext? rowContext)
    {
        _values = values;
        _declared = declared;
        _row = row;
        _rowContext = rowContext;
    }

    public bool IsEmpty => _values is null && _row is null;

    /// <summary>
    /// Resolves both slots and asks each what it wants, once. Returns a pipeline that does nothing when
    /// neither is bound, so the caller needs no branch and the no-transform case allocates nothing.
    /// </summary>
    public static TransformPipeline Build(
        ScriptHost host,
        ConnectionConfig? connection,
        ReplicationTaskConfig? task,
        TableMappingConfig? mapping,
        IReadOnlyList<ColumnMapping> columnMappings,
        IScriptDialect dialect,
        Action<string> log)
    {
        var valueBinding = host.ResolveBinding<IValueColumnExpression>(
            ScriptSlots.ValueColumnExpression, connection, task, mapping);
        var rowBinding = host.ResolveBinding<IRowTransform>(ScriptSlots.RowTransform, connection, task, mapping);

        IReadOnlyList<ValueColumnExpressionContext> declared = [];

        if (valueBinding is not null)
        {
            var declarationContext = new ValueColumnDeclarationContext(columnMappings, dialect, valueBinding.Value.Parameters);
            var names = Guarded(() => valueBinding.Value.Script.DeclareColumns(declarationContext), "declaring its columns")
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            // One context per *declared* column, built once — the alternative is allocating a record
            // per cell, which is exactly the cost this slot is already the expensive one for.
            declared = columnMappings
                .Where(m => names.Contains(m.SourceColumn))
                .DistinctBy(m => m.SourceColumn, StringComparer.OrdinalIgnoreCase)
                .Select(m => new ValueColumnExpressionContext(
                    m.SourceColumn, m.TargetColumn, dialect, valueBinding.Value.Parameters))
                .ToList();

            // A script that declares nothing is a script that never runs.
            if (declared.Count == 0)
                valueBinding = null;
        }

        var rowContext = rowBinding is null
            ? null
            : new RowTransformContext(columnMappings, dialect, rowBinding.Value.Parameters, log);

        return new TransformPipeline(valueBinding?.Script, declared, rowBinding?.Script, rowContext);
    }

    /// <summary>
    /// The transformed stream. <paramref name="rows"/> is consumed lazily, so a transform never buffers
    /// a change set — the pipeline stays streaming end to end, as it has since phase 3.
    /// </summary>
    public async IAsyncEnumerable<ChangeRow> ApplyAsync(
        IAsyncEnumerable<ChangeRow> rows,
        Action<long>? reportDropped = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        long dropped = 0;
        ValueTarget[]? valueTargets = null;

        await foreach (var row in rows.WithCancellation(cancellationToken))
        {
            // Resolved against the first row's schema, like every other ordinal cache in this codebase
            // (ChangeRowDataReader, BatchInsertStagingProvider) — the layout is per read, not per row.
            if (valueTargets is null)
            {
                valueTargets = ResolveValueTargets(row.Schema);

                // Declared before the first row is yielded, because staging builds its table from the
                // schema of the row it sees first — a column added without saying so would be dropped.
                if (_row is not null && _rowContext is not null)
                    Guarded(() => _row.DeclareSchema(row.Schema, _rowContext), "declaring its output schema");
            }

            var current = row;

            // A delete carries only its key; every other slot is null, and the writers key off the
            // operation rather than the values. Handing those nulls to a transform expecting values is
            // a trap with no upside, since the result would be discarded anyway.
            if (valueTargets.Length > 0 && current.Operation != ChangeOperation.Delete)
                current = ApplyValues(current, valueTargets);

            if (_row is not null && _rowContext is not null)
            {
                var transformed = await Guarded(
                    () => _row.TransformAsync(current, _rowContext, cancellationToken), "transforming a row");

                if (transformed is null)
                {
                    dropped++;
                    continue;
                }

                current = transformed;
            }

            yield return current;
        }

        // Rows read and rows staged diverging is correct when something filtered; it is also invisible
        // unless someone says so.
        if (dropped > 0)
            reportDropped?.Invoke(dropped);
    }

    private ChangeRow ApplyValues(ChangeRow row, ValueTarget[] targets)
    {
        object?[]? values = null;

        foreach (var (ordinal, context) in targets)
        {
            var original = row.Values[ordinal];
            var transformed = Guarded(
                () => _values!.Evaluate(original, context), $"transforming column '{context.SourceColumn}'");

            // A transform that returns what it was given costs nothing beyond the call — no copy, no
            // new row. Passing a column through is the common case for a script that declares several.
            if (Equals(original, transformed))
                continue;

            values ??= (object?[])row.Values.Clone();
            values[ordinal] = transformed;
        }

        return values is null ? row : row with { Values = values };
    }

    /// <summary>
    /// Where the declared columns sit in this read's result. Resolved against the first row's schema,
    /// like every other ordinal cache here (`ChangeRowDataReader`, `BatchInsertStagingProvider`) — a
    /// layout belongs to a read, not to a row.
    /// <para>
    /// A declared column the reader did not return is dropped rather than throwing: a mapping's
    /// projection may legitimately not carry it, and failing the run over a transform that has nothing
    /// to transform would be worse than doing nothing.
    /// </para>
    /// </summary>
    private ValueTarget[] ResolveValueTargets(ChangeSchema schema)
    {
        if (_values is null)
            return [];

        return _declared
            .Select(c => schema.TryGetOrdinal(c.SourceColumn, out var ordinal) ? new ValueTarget(ordinal, c) : default)
            .Where(t => t.Context is not null)
            .ToArray();
    }

    private static T Guarded<T>(Func<T> action, string what)
    {
        try
        {
            return action();
        }
        catch (Exception ex) when (ex is not ScriptExecutionException)
        {
            throw new ScriptExecutionException($"A transform script threw while {what}: {ex.Message}", ex);
        }
    }
}
