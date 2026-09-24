using DbDataSync.Core.Config;
using DbDataSync.Drivers.Abstractions;
using DbDataSync.Core.Sql;

namespace DbDataSync.Drivers.Generic;

/// <summary>
/// Builds the watermark reader's two statements. Separated from the reader — following
/// <c>MsSqlChangeTrackingStatement</c>'s precedent — so the exact text can be asserted without a live
/// server, which is how "moving this reader onto a dialect changed no SQL" is a checked claim rather
/// than an assertion.
/// </summary>
public static class WatermarkStatement
{
    public const string PreviousWatermarkParameter = "previousWatermark";

    /// <summary>
    /// The highest watermark currently present, which becomes the run's new watermark.
    /// <para>
    /// **Phase 191S**: <paramref name="query"/>, when set, always wraps — like
    /// <see cref="BatchReloadStatement.BuildRange"/>, there is no unwrapped alternative for an
    /// aggregate.
    /// </para>
    /// <para>
    /// **Phase 192S**: <paramref name="transform"/>, when the watermark column also carries a
    /// <see cref="ColumnMapping.Transform"/>, makes the adopted maximum agree with the transformed value
    /// <see cref="SourceProjection"/> actually projects — otherwise a fresh <c>ChangesFromLatest</c>
    /// adoption would record a position in the wrong value-space. **Not applied** by
    /// <see cref="WatermarkReader"/>'s own <c>IPositionCapturing.CapturePositionAsync</c> path, which has
    /// no <c>ColumnMapping</c> list to consult — a named, documented gap, not an oversight.
    /// </para>
    /// </summary>
    public static string BuildMaxWatermark(
        SqlDialect dialect, string schema, string table, string? query, string watermarkColumn, string? filter,
        string? transform = null)
    {
        var sourceExpression = query is not null ? $"({query})" : dialect.QualifyTable(schema, table);
        var fromTable = query is not null ? $"{sourceExpression} AS base" : sourceExpression;
        Func<string, string> reference = query is not null ? c => $"base.{dialect.QuoteIdentifier(c)}" : dialect.QuoteIdentifier;
        var quotedColumn = SourceProjection.RenderExpression(watermarkColumn, transform, reference);
        var filterClause = string.IsNullOrWhiteSpace(filter) ? "" : $" WHERE {filter}";
        return $"SELECT MAX({quotedColumn}) FROM {fromTable}{filterClause}";
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
    /// <summary>
    /// <paramref name="relationships"/>/<paramref name="relationshipAliases"/> (phase 188J) add one
    /// <c>LEFT JOIN</c> per relationship actually referenced by a <see cref="ColumnMapping"/> — see
    /// <see cref="RelationshipAliases.Assign"/>/<see cref="RelationshipJoins.Render"/>. The primary table
    /// only gets its own <c>AS base</c> alias, and <paramref name="watermarkColumn"/> only gets qualified
    /// through it, once at least one join is present — matching
    /// <see cref="BatchReloadStatement.BuildRead"/>'s identical treatment, and for the identical reason:
    /// a mapping with no relationships renders byte-for-byte what it always has.
    /// <para>
    /// **Phase 191S**: <paramref name="query"/>, when set, always wraps, with no unwrapped alternative —
    /// unlike a reload, watermark reading needs <c>ORDER BY</c> for its tie-safe bounded-read guarantee
    /// on essentially every pass, including the first, so there is nothing to conditionally skip. Safe to
    /// do unconditionally because a query-shaped source with subqueries disallowed can never reach this
    /// reader in the first place — <c>ConfigValidation.ValidateQuerySourceReader</c> rejects that
    /// combination outright at save.
    /// </para>
    /// </summary>
    public static string BuildRead(
        SqlDialect dialect, string schema, string table, string? query, string watermarkColumn, bool hasPreviousWatermark,
        string? filter, string projection = "*", bool bounded = false,
        IReadOnlyList<RelationshipConfig>? relationships = null,
        IReadOnlyDictionary<string, string>? relationshipAliases = null,
        string? transform = null)
    {
        var joins = RelationshipJoins.Render(dialect, relationships, relationshipAliases);
        var needsAlias = joins.Length > 0 || query is not null;
        var sourceExpression = query is not null ? $"({query})" : dialect.QualifyTable(schema, table);
        var fromTable = needsAlias ? $"{sourceExpression} AS base" : sourceExpression;
        Func<string, string> reference = needsAlias ? c => $"base.{dialect.QuoteIdentifier(c)}" : dialect.QuoteIdentifier;
        var quotedColumn = SourceProjection.RenderExpression(watermarkColumn, transform, reference);
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
            SELECT {limitPrefix}{projection}{position} FROM {fromTable}{joins}
            WHERE {predicate}{userFilter}
            ORDER BY {quotedColumn}{limitSuffix}
            """;
    }
}
