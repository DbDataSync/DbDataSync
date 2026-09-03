using System.Data.Common;
using DbDataSync.Core.Config;
using DbDataSync.Drivers.Abstractions;
using DbDataSync.Core.Sql;

namespace DbDataSync.Drivers.Generic;

/// <summary>
/// A rendered <see cref="BatchReloadSegment"/>: the SQL predicate limiting a statement to that
/// segment's rows, plus the parameters it references. Every bound value is a typed parameter — never
/// spliced into the text.
/// <para>
/// The predicate's *shape* — <c>1 = 1</c>, <c>IN (…)</c>, half-open bounds — is identical on every
/// engine, which is why it lives here. Only quoting and placeholder syntax vary, and those come from
/// the <see cref="SqlDialect"/>. Turning a bound into a typed parameter stays with the driver
/// (<see cref="ISegmentValueBinder"/>): that is where engines genuinely diverge, because a provider's
/// type enum has no engine-neutral equivalent.
/// </para>
/// </summary>
public sealed record SegmentScope(string Predicate, IReadOnlyList<DbParameter> Parameters)
{
    /// <summary>Matches every row. Used for <see cref="FullSegment"/> and for an unsegmented unit of
    /// work, so callers can always interpolate a predicate rather than conditionally emitting the
    /// whole WHERE clause.</summary>
    public static SegmentScope All { get; } = new("1 = 1", []);

    public void AddTo(DbCommand command)
    {
        foreach (var parameter in Parameters)
            command.Parameters.Add(parameter);
    }

    /// <summary>
    /// Renders <paramref name="segment"/> against the table described by <paramref name="columns"/>
    /// (the source table for a reader, the target table for a writer — the segment column must exist
    /// on whichever side is being scoped).
    /// <para>
    /// Bound values arrive as strings from a web form or from system-computed bucket boundaries, so
    /// they're converted to the segment column's actual type and bound as parameters. This is
    /// deliberately stricter than <c>SourceTableRef.Filter</c>, which is a raw predicate an
    /// administrator hand-authors in config; segment bounds are ordinary user input.
    /// </para>
    /// </summary>
    /// <param name="columnMappings">
    /// Supplied by writers, omitted by readers. A segment names a column on the *source* table, but a
    /// writer scopes the *target* — and a mapping is free to rename a column across the two. Passing
    /// the mappings translates the segment's column name to its target-side counterpart, so a reload
    /// segmented on a renamed column scopes the same rows on both sides instead of failing to find the
    /// column (or, worse, finding an unrelated target column that happens to share the source name).
    /// </param>
    public static SegmentScope Build(
        SqlDialect dialect,
        ISegmentValueBinder binder,
        BatchReloadSegment? segment,
        IReadOnlyList<ColumnMetadata> columns,
        IReadOnlyList<ColumnMapping>? columnMappings = null) =>
        segment switch
        {
            null or FullSegment => All,
            ListSegment list => BuildList(dialect, binder, list, ResolveColumn(list.Column, columns, columnMappings)),
            RangeSegment range => BuildRange(dialect, binder, range, ResolveColumn(range.Column, columns, columnMappings)),
            AutoSegment auto => throw new InvalidOperationException(
                $"Auto segment on '{auto.Column}' reached execution unexpanded. Auto segments must be " +
                "expanded into concrete ranges (ISegmentExpandingReader.ExpandAutoSegmentsAsync) when " +
                "the work is enqueued, never carried through as a runtime segment."),
            _ => throw new ArgumentOutOfRangeException(nameof(segment), segment, "Unknown segment mode."),
        };

    private static SegmentScope BuildList(
        SqlDialect dialect, ISegmentValueBinder binder, ListSegment list, ColumnMetadata column)
    {
        if (list.Values.Count == 0)
            throw new InvalidOperationException(
                $"List segment on '{list.Column}' has no values. An empty list matches nothing, which " +
                "for a reconciling writer would delete the target's entire scope — reject it rather " +
                "than render 'IN ()' (which isn't valid SQL anyway).");

        var parameters = list.Values
            .Select((value, i) => binder.CreateParameter(dialect.ParameterName($"__seg{i}"), value, column))
            .ToList();

        // Placeholders come from the dialect rather than from each parameter's own name: the two are
        // the same string on SQL Server, but not on an engine whose bound parameter name drops the
        // sigil its statement text requires.
        var placeholders = string.Join(", ", Enumerable.Range(0, parameters.Count).Select(i => dialect.ParameterReference($"__seg{i}")));
        return new SegmentScope($"{dialect.QuoteIdentifier(column.Name)} IN ({placeholders})", parameters);
    }

    private static SegmentScope BuildRange(
        SqlDialect dialect, ISegmentValueBinder binder, RangeSegment range, ColumnMetadata column)
    {
        var quoted = dialect.QuoteIdentifier(column.Name);
        // Half-open: consecutive ranges tile a value space with no gap and no row processed twice.
        return new SegmentScope(
            $"{quoted} >= {dialect.ParameterReference("__segMin")} AND {quoted} < {dialect.ParameterReference("__segMax")}",
            [
                binder.CreateParameter(dialect.ParameterName("__segMin"), range.RangeMin, column),
                binder.CreateParameter(dialect.ParameterName("__segMax"), range.RangeMax, column),
            ]);
    }

    private static ColumnMetadata ResolveColumn(
        string columnName, IReadOnlyList<ColumnMetadata> columns, IReadOnlyList<ColumnMapping>? columnMappings)
    {
        var resolvedName = columnMappings?
            .FirstOrDefault(m => string.Equals(m.SourceColumn, columnName, StringComparison.OrdinalIgnoreCase))?
            .TargetColumn ?? columnName;

        return columns.FirstOrDefault(c => string.Equals(c.Name, resolvedName, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException(
                $"Segment column '{resolvedName}' was not found (available: {string.Join(", ", columns.Select(c => c.Name))}).");
    }
}

/// <summary>
/// Turns a segment's string bound into a parameter typed to match the column it is compared against.
/// Binding a bound as an untyped string would make the engine convert on the *column* side of the
/// comparison for anything non-textual, which both changes the comparison's semantics and prevents an
/// index seek — the opposite of what segment scoping exists to achieve.
/// <para>
/// Per-driver rather than on the dialect: the mapping's *shape* generalises, but its output is a
/// provider type enum (<c>SqlDbType</c>, <c>NpgsqlDbType</c>, …) with no common ancestor. Extract a
/// shared implementation only once two drivers demonstrably agree.
/// </para>
/// </summary>
public interface ISegmentValueBinder
{
    /// <param name="name">Already rendered through <see cref="SqlDialect.ParameterName"/>.</param>
    DbParameter CreateParameter(string name, string rawValue, ColumnMetadata column);
}
