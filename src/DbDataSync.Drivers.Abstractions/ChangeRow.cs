namespace DbDataSync.Drivers.Abstractions;

public enum ChangeOperation
{
    Insert,
    Update,
    Delete,
}

/// <summary>
/// The column layout every row in one read shares — built once by the reader, referenced by every
/// <see cref="ChangeRow"/> it produces.
/// <para>
/// Hoisting the names out of the rows is the point: a change set has one layout and millions of rows,
/// so carrying the names per row meant a dictionary per row, a hash insert per cell on the way in and
/// a hash lookup per cell on the way out. Resolving a column to an ordinal once per run turns every
/// subsequent access into an array index. See
/// architecture/planning/todo/optimize-in-memory-data-column-oriented.md for the measurements.
/// </para>
/// </summary>
public sealed class ChangeSchema
{
    private readonly Dictionary<string, int> _ordinalByName;

    public ChangeSchema(IReadOnlyList<string> columnNames)
    {
        ColumnNames = columnNames;
        // Case-insensitive: SQL Server resolves column names that way by default, and the rest of this
        // driver layer already compares them with OrdinalIgnoreCase.
        _ordinalByName = new Dictionary<string, int>(columnNames.Count, StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < columnNames.Count; i++)
            _ordinalByName[columnNames[i]] = i;
    }

    public IReadOnlyList<string> ColumnNames { get; }

    public int Count => ColumnNames.Count;

    public bool TryGetOrdinal(string columnName, out int ordinal) =>
        _ordinalByName.TryGetValue(columnName, out ordinal);

    /// <summary>
    /// Resolves a column name, or throws naming what the source actually returned.
    /// <para>
    /// Previously a mapping naming a column the source doesn't produce was a silent dictionary miss
    /// that wrote NULL into the target. It is a configuration error and now says so.
    /// </para>
    /// </summary>
    public int GetOrdinal(string columnName) =>
        TryGetOrdinal(columnName, out var ordinal)
            ? ordinal
            : throw new InvalidOperationException(
                $"Source column '{columnName}' was not returned by the reader " +
                $"(available: {string.Join(", ", ColumnNames)}).");
}

/// <summary>
/// One row-level change, held as values positioned by <see cref="Schema"/> rather than keyed by name.
/// <para>
/// For <see cref="ChangeOperation.Delete"/>, most readers (e.g. SQL Server Change Tracking) can only
/// recover the primary key columns — the rest of the row is gone by the time it's read — so every
/// other slot is null for a delete. Writers must handle that.
/// </para>
/// <para>
/// <see cref="Values"/> belongs to this row alone and is safe to hold: a reader must not hand the same
/// array to more than one row. That costs an array per row, which is what lets a staging provider
/// buffer rows — a columnar one would have to.
/// </para>
/// </summary>
public sealed record ChangeRow(ChangeOperation Operation, ChangeSchema Schema, object?[] Values)
{
    public object? this[int ordinal] => Values[ordinal];

    /// <summary>By name, for callers that have one rather than an ordinal. Throws for a column the
    /// schema doesn't have; a column that exists but wasn't populated (a delete's non-key columns)
    /// reads as null.</summary>
    public object? this[string columnName] => Values[Schema.GetOrdinal(columnName)];
}
