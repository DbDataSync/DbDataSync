using DataSync.Drivers.Abstractions;
using DataSync.Core.Sql;

namespace DataSync.Drivers.Generic;

/// <summary>
/// Builds the watermark reader's two statements. Separated from the reader — following
/// <c>MsSqlChangeTrackingStatement</c>'s precedent — so the exact text can be asserted without a live
/// server, which is how "moving this reader onto a dialect changed no SQL" is a checked claim rather
/// than an assertion.
/// </summary>
public static class WatermarkStatement
{
    public const string PreviousWatermarkParameter = "previousWatermark";

    /// <summary>The highest watermark currently present, which becomes the run's new watermark.</summary>
    public static string BuildMaxWatermark(SqlDialect dialect, string schema, string table, string watermarkColumn, string? filter)
    {
        var filterClause = string.IsNullOrWhiteSpace(filter) ? "" : $" WHERE {filter}";
        return
            $"SELECT MAX({dialect.QuoteIdentifier(watermarkColumn)}) FROM " +
            $"{dialect.QualifyTable(schema, table)}{filterClause};";
    }

    /// <summary>
    /// The rows to emit. With no previous watermark the predicate is <c>1 = 1</c> rather than an
    /// omitted WHERE clause, so a configured filter can always be appended with <c>AND</c>.
    /// </summary>
    /// <param name="projection">
    /// The SELECT list from <see cref="SourceProjection"/>. Defaults to <c>*</c>, which is what this
    /// built before mappings reached the reader and what it still builds when none are supplied.
    /// <para>
    /// The watermark column need not be in it: <c>ORDER BY</c> on a column outside the select list is
    /// valid SQL, and a mapping that does not carry its own watermark column is perfectly ordinary.
    /// </para>
    /// </param>
    /// <param name="bounded">
    /// Caps the read at <see cref="BoundedRead.RowLimitParameter"/> rows, ties included, and carries
    /// the watermark column back beside each row under <see cref="BoundedRead.PositionColumn"/>.
    /// <para>
    /// The extra column is not redundant with the projection: the watermark column need not be mapped,
    /// so a bounded read cannot count on finding it there — and it is the one value the read must have,
    /// since the last row's copy of it *is* the new watermark. Appended last, so every existing ordinal
    /// stays where it was.
    /// </para>
    /// </param>
    public static string BuildRead(
        SqlDialect dialect, string schema, string table, string watermarkColumn, bool hasPreviousWatermark,
        string? filter, string projection = "*", bool bounded = false)
    {
        var quotedColumn = dialect.QuoteIdentifier(watermarkColumn);
        var predicate = hasPreviousWatermark
            ? $"{quotedColumn} > {dialect.ParameterReference(PreviousWatermarkParameter)}"
            : "1 = 1";
        var userFilter = string.IsNullOrWhiteSpace(filter) ? "" : $" AND ({filter})";

        var (limitPrefix, limitSuffix) = bounded
            ? dialect.RenderTieSafeRowLimit(BoundedRead.RowLimitParameter)
            : ("", "");
        var position = bounded
            ? $", {quotedColumn} AS {dialect.QuoteIdentifier(BoundedRead.PositionColumn)}"
            : "";

        return $"""
            SELECT {limitPrefix}{projection}{position} FROM {dialect.QualifyTable(schema, table)}
            WHERE {predicate}{userFilter}
            ORDER BY {quotedColumn}{limitSuffix};
            """;
    }
}
