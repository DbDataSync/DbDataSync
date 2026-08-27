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
    public static string BuildRead(
        SqlDialect dialect, string schema, string table, string watermarkColumn, bool hasPreviousWatermark, string? filter)
    {
        var quotedColumn = dialect.QuoteIdentifier(watermarkColumn);
        var predicate = hasPreviousWatermark
            ? $"{quotedColumn} > {dialect.ParameterReference(PreviousWatermarkParameter)}"
            : "1 = 1";
        var userFilter = string.IsNullOrWhiteSpace(filter) ? "" : $" AND ({filter})";

        return $"""
            SELECT * FROM {dialect.QualifyTable(schema, table)}
            WHERE {predicate}{userFilter}
            ORDER BY {quotedColumn};
            """;
    }
}
