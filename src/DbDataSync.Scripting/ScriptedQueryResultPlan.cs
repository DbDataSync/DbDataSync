using DbDataSync.Drivers.Abstractions;
using DbDataSync.Scripting.Abstractions;

namespace DbDataSync.Scripting;

/// <summary>
/// How one scripted query's result set is read back: which ordinal carries the operation, how its
/// values decode, and which columns are the mechanism's own bookkeeping rather than data.
/// <para>
/// Resolved once per pass against the result set's real shape, so the per-row path is an array index
/// rather than a dictionary lookup — the same reason <c>ChangeSchema</c> exists at all (phase 14).
/// </para>
/// </summary>
internal sealed class ScriptedQueryResultPlan
{
    private readonly int _operationOrdinal;
    private readonly IReadOnlyDictionary<string, ChangeOperation> _operationValues;
    private readonly ChangeOperation _defaultOperation;
    private readonly int[] _keptOrdinals;

    private ScriptedQueryResultPlan(
        ChangeSchema schema,
        int operationOrdinal,
        IReadOnlyDictionary<string, ChangeOperation> operationValues,
        ChangeOperation defaultOperation,
        int[] keptOrdinals)
    {
        Schema = schema;
        _operationOrdinal = operationOrdinal;
        _operationValues = operationValues;
        _defaultOperation = defaultOperation;
        _keptOrdinals = keptOrdinals;
    }

    /// <summary>The schema staging will see — the result set's columns less the excluded ones and the
    /// operation column itself.</summary>
    public ChangeSchema Schema { get; }

    public static ScriptedQueryResultPlan Build(ChangeSchema resultSchema, SourceQueryShape shape)
    {
        var excluded = new HashSet<string>(shape.ExcludeColumns ?? [], StringComparer.OrdinalIgnoreCase);
        if (shape.OperationColumn is not null)
            excluded.Add(shape.OperationColumn);

        var operationOrdinal = -1;
        if (shape.OperationColumn is not null
            && !resultSchema.TryGetOrdinal(shape.OperationColumn, out operationOrdinal))
        {
            throw new ScriptExecutionException(
                $"The query's result has no '{shape.OperationColumn}' column to read the operation from " +
                $"(it returned: {string.Join(", ", resultSchema.ColumnNames)}).");
        }

        var kept = Enumerable.Range(0, resultSchema.Count)
            .Where(i => !excluded.Contains(resultSchema.ColumnNames[i]))
            .ToArray();

        return new ScriptedQueryResultPlan(
            new ChangeSchema([.. kept.Select(i => resultSchema.ColumnNames[i])]),
            operationOrdinal,
            shape.OperationValues is null
                ? new Dictionary<string, ChangeOperation>(StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, ChangeOperation>(shape.OperationValues, StringComparer.OrdinalIgnoreCase),
            shape.DefaultOperation,
            kept);
    }

    /// <summary>
    /// A value the map does not cover falls back to the default rather than throwing. A change feed
    /// that grows a fifth operation code should not stop a replication dead; the operator sees rows
    /// arriving as the default and can extend the map.
    /// </summary>
    public ChangeOperation OperationOf(object?[] values)
    {
        if (_operationOrdinal < 0)
            return _defaultOperation;

        var raw = values[_operationOrdinal];
        if (raw is null)
            return _defaultOperation;

        var key = Convert.ToString(raw, System.Globalization.CultureInfo.InvariantCulture) ?? "";
        return _operationValues.TryGetValue(key, out var operation) ? operation : _defaultOperation;
    }

    /// <summary>The row's values, less the bookkeeping. Returns the array unchanged when nothing is
    /// excluded, so the common case allocates nothing extra.</summary>
    public object?[] Project(object?[] values)
    {
        if (_keptOrdinals.Length == values.Length)
            return values;

        var projected = new object?[_keptOrdinals.Length];
        for (var i = 0; i < _keptOrdinals.Length; i++)
            projected[i] = values[_keptOrdinals[i]];
        return projected;
    }
}
