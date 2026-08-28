using DataSync.Core.Config;

namespace DataSync.Drivers.Generic;

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
        SqlDialect dialect, string schema, string table, IReadOnlyList<VerificationColumn> groupBy, string? filter)
    {
        var selected = groupBy
            .Select(g => $"{g.Expression} AS {dialect.QuoteIdentifier(g.ResultName)}")
            .Append($"COUNT(*) AS {dialect.QuoteIdentifier(RowCountColumn)}");

        return Assemble(dialect, schema, table, selected, groupBy, filter);
    }

    /// <summary>
    /// The sum of each measure, grouped by whatever the check named. The measure keeps its result name,
    /// so a check summing <c>Amount</c> comes back as <c>Amount</c> from both sides regardless of what
    /// the column is called on either.
    /// </summary>
    public static string BuildSum(
        SqlDialect dialect, string schema, string table,
        IReadOnlyList<VerificationColumn> groupBy, IReadOnlyList<VerificationColumn> measures, string? filter)
    {
        if (measures.Count == 0)
            throw new ConfigValidationException("A Sum check needs at least one measure column.");

        var selected = groupBy
            .Select(g => $"{g.Expression} AS {dialect.QuoteIdentifier(g.ResultName)}")
            .Concat(measures.Select(m => $"SUM({m.Expression}) AS {dialect.QuoteIdentifier(m.ResultName)}"));

        return Assemble(dialect, schema, table, selected, groupBy, filter);
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
        IEnumerable<string> selected, IReadOnlyList<VerificationColumn> groupBy, string? filter)
    {
        var sql = $"SELECT {string.Join(", ", selected)} FROM {dialect.QualifyTable(schema, table)}";

        if (!string.IsNullOrWhiteSpace(filter))
            sql += $" WHERE {filter}";

        if (groupBy.Count > 0)
        {
            var expressions = string.Join(", ", groupBy.Select(g => g.Expression));
            sql += $" GROUP BY {expressions} ORDER BY {expressions}";
        }

        return sql + ";";
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
