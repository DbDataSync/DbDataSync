using DbDataSync.Core.Config;
using DbDataSync.Core.Sql;

namespace DbDataSync.Drivers.Generic;

/// <summary>
/// The statements a built-in verification check runs — see phase 43. Separated from whatever executes
/// them, in the same style as <see cref="WatermarkStatement"/>, <see cref="StagingStatement"/> and
/// <see cref="BatchReloadStatement"/>: a pure function of the mapping, assertable without a server.
/// <para>
/// Both sides are generated from the **same** check, and the difference between them is entirely in
/// what a column is called and what a transform does to it. That is the point — a check names its
/// columns once, by their target names, and each side's statement is derived. Two hand-written
/// statements are what a Sql check is for, and they are the operator's problem to keep in step.
/// </para>
/// </summary>
public static class VerificationStatement
{
    /// <summary>The column a row count comes back in. Fixed rather than derived from anything, because
    /// both sides have to agree on it and there is no column for it to be named after.</summary>
    public const string RowCountColumn = "__rows";

    /// <summary>
    /// Rows, grouped by whatever the check named.
    /// <para>
    /// Ordered by the grouping columns, which is not decoration: the two result sets are compared by a
    /// merge join, so both sides arriving in the same order is what lets the comparison hold one row
    /// from each side rather than the whole of both.
    /// </para>
    /// </summary>
    public static string BuildRowCount(
        SqlDialect dialect, string schema, string table, IReadOnlyList<VerificationColumn> groupBy,
        string? filter, string? currentOnly = null)
    {
        var selected = groupBy
            .Select(g => $"{g.Expression} AS {dialect.QuoteIdentifier(g.ResultName)}")
            .Append($"COUNT(*) AS {dialect.QuoteIdentifier(RowCountColumn)}");

        return Assemble(dialect, schema, table, selected, groupBy, filter, currentOnly);
    }

    /// <summary>
    /// The sum of each measure, grouped by whatever the check named. The measure keeps its result name,
    /// so a check summing <c>Amount</c> comes back as <c>Amount</c> from both sides regardless of what
    /// the column is called on either.
    /// </summary>
    public static string BuildSum(
        SqlDialect dialect, string schema, string table,
        IReadOnlyList<VerificationColumn> groupBy, IReadOnlyList<VerificationColumn> measures,
        string? filter, string? currentOnly = null)
    {
        if (measures.Count == 0)
            throw new ConfigValidationException("A Sum check needs at least one measure column.");

        var selected = groupBy
            .Select(g => $"{g.Expression} AS {dialect.QuoteIdentifier(g.ResultName)}")
            .Concat(measures.Select(m => $"SUM({m.Expression}) AS {dialect.QuoteIdentifier(m.ResultName)}"));

        return Assemble(dialect, schema, table, selected, groupBy, filter, currentOnly);
    }

    /// <summary>
    /// The predicate that narrows a historized target to what is current, or null.
    ///
    /// <para>
    /// **Two shapes, because the two writers historize differently.** An SCD Type 2 target has a
    /// per-row flag and one current row per key. A Snapshot target has no flag at all — every snapshot
    /// is a complete separate copy — so "current" there means the most recent marker value, which is a
    /// subquery rather than a comparison.
    /// </para>
    ///
    /// <para>
    /// The subquery is <c>= (SELECT MAX(marker) FROM …)</c> rather than a join or a window function:
    /// the marker is one value per pass, so the planner reads it once, and it says what it means to
    /// somebody reading the generated SQL.
    /// </para>
    /// </summary>
    public static string? CurrentOnlyPredicate(SqlDialect dialect, string schema, string table, string? currentColumn)
    {
        if (string.IsNullOrWhiteSpace(currentColumn))
            return null;

        var quoted = dialect.QuoteIdentifier(currentColumn);

        // A snapshot marker is a time, not a flag: the newest value is the newest copy.
        if (string.Equals(currentColumn, HistorizedColumns.SnapshotAt, StringComparison.OrdinalIgnoreCase))
            return $"{quoted} = (SELECT MAX({quoted}) FROM {dialect.QualifyTable(schema, table)})";

        return $"{quoted} = {dialect.TrueLiteral}";
    }

    /// <summary>
    /// <c>GROUP BY</c> repeats the expressions rather than referencing the aliases: an alias is not in
    /// scope in <c>GROUP BY</c> on SQL Server, and the engines that do allow it are the exception.
    /// <para>
    /// <paramref name="filter"/> is admin-authored raw SQL on the same footing as a mapping's own
    /// source filter — an arbitrary predicate cannot be parameterised, and whoever writes one can
    /// already point a connection anywhere.
    /// </para>
    /// </summary>
    private static string Assemble(
        SqlDialect dialect, string schema, string table,
        IEnumerable<string> selected, IReadOnlyList<VerificationColumn> groupBy, string? filter,
        string? currentOnly = null)
    {
        var sql = $"SELECT {string.Join(", ", selected)} FROM {dialect.QualifyTable(schema, table)}";

        // Both, when both apply — the check's own filter narrows what is compared and the current-only
        // predicate narrows which version of it. Parenthesised, because an operator's filter is an
        // arbitrary expression and one containing an OR would otherwise swallow the AND.
        var predicates = new List<string>();
        if (!string.IsNullOrWhiteSpace(filter))
            predicates.Add($"({filter})");
        if (!string.IsNullOrWhiteSpace(currentOnly))
            predicates.Add(currentOnly);

        if (predicates.Count > 0)
            sql += $" WHERE {string.Join(" AND ", predicates)}";

        if (groupBy.Count > 0)
        {
            var expressions = string.Join(", ", groupBy.Select(g => g.Expression));
            sql += $" GROUP BY {expressions} ORDER BY {expressions}";
        }

        return sql;
    }

    /// <summary>
    /// Every column a check names, resolved for one side: what to write in the statement, and what the
    /// result column is called.
    /// </summary>
    /// <param name="Expression">Already quoted, and already transformed on the source side.</param>
    /// <param name="ResultName">The **target** column's name, on both sides, so the two result sets
    /// have the same shape and can be compared column by column rather than position by position.</param>
    public readonly record struct VerificationColumn(string Expression, string ResultName);

    /// <summary>
    /// Turns the target column names a check declares into the columns to select, for one side.
    /// <para>
    /// On the source that means finding the mapping that feeds each named target column and rendering
    /// its expression — transform included, because a check grouping by a transformed column has to
    /// group by what the transform produced or it reports a difference that is not one. On the target
    /// it is the column itself.
    /// </para>
    /// </summary>
    public static IReadOnlyList<VerificationColumn> ResolveSource(
        SqlDialect dialect, IReadOnlyList<ColumnMapping> columnMappings, IReadOnlyList<string> targetColumns)
    {
        return
        [
            .. targetColumns.Select(name =>
            {
                var mapping = columnMappings.FirstOrDefault(
                    m => string.Equals(m.TargetColumn, name, StringComparison.OrdinalIgnoreCase));

                if (mapping is null)
                {
                    throw new ConfigValidationException(
                        $"Verification names column '{name}', which this mapping does not write to the target. " +
                        "A check compares mapped columns; there is nothing on the source to compare against.");
                }

                return new VerificationColumn(
                    SourceProjection.RenderExpression(mapping, dialect.QuoteIdentifier), name);
            }),
        ];
    }

    public static IReadOnlyList<VerificationColumn> ResolveTarget(
        SqlDialect dialect, IReadOnlyList<string> targetColumns) =>
        [.. targetColumns.Select(name => new VerificationColumn(dialect.QuoteIdentifier(name), name))];
}
