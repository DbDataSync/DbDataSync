using DataSync.Drivers.Abstractions;

namespace DataSync.Drivers.MsSql;

/// <summary>
/// Builds the incremental Change Tracking query. Separated from the reader so the select list — the
/// part that had a real defect in it — can be asserted without a live SQL Server.
/// </summary>
public static class MsSqlChangeTrackingStatement
{
    /// <summary>Computed marker, not a real column: 1 when the LEFT JOIN found no source row.</summary>
    public const string BaseMissingColumn = "__BaseMissing";

    /// <summary>Fixed leading ordinals, ahead of the primary key columns.</summary>
    public const int OperationOrdinal = 0;
    public const int BaseMissingOrdinal = 1;
    public const int FirstKeyOrdinal = 2;

    /// <summary>
    /// The first pass, before any watermark exists: every row, as an insert. Extracted from the reader
    /// so the preview and the run build it in one place — a preview that re-derives a statement is a
    /// statement that can drift from the one that runs.
    /// </summary>
    /// <param name="filter">
    /// An admin-authored raw predicate from <c>TableMappingConfig</c>, not end-user input — see the
    /// type's XML doc. It cannot be parameterized, being an arbitrary boolean expression rather than
    /// a value.
    /// </param>
    public static string BuildFullLoad(string schema, string table, string projection, string? filter)
    {
        var filterClause = string.IsNullOrWhiteSpace(filter) ? "" : $" WHERE {filter}";
        return $"SELECT {projection} FROM {SqlIdentifier.Quote(schema)}.{SqlIdentifier.Quote(table)}{filterClause};";
    }

    /// <summary>
    /// Every column is named explicitly, and the primary key is taken **only** from CHANGETABLE.
    /// <para>
    /// The previous form selected <c>CT.[pk], base.*</c>, which re-emitted the key so it appeared
    /// twice in the result set. The reader maps columns into a dictionary by name, so the second
    /// occurrence — base's — overwrote the first. For a row whose source had since been deleted that
    /// set the key to NULL, destroying the one value that was still reliable and turning a skippable
    /// row into a failed run. CHANGETABLE always has the key; base may not.
    /// </para>
    /// </summary>
    /// <param name="renderNonKeyColumn">
    /// How each non-key column is written. Plain <c>base.[Col]</c> by default; a mapping carrying a
    /// <see cref="ColumnMapping.Transform"/> renders an expression aliased back to the column's own
    /// name. Taking it as a function rather than a list of names is what keeps this method's ordinal
    /// arithmetic — and therefore <see cref="BaseMissingOrdinal"/> — unchanged.
    /// </param>
    /// <param name="bounded">
    /// Caps the window at <see cref="BoundedRead.RowLimitParameter"/> rows, ties on
    /// <c>SYS_CHANGE_VERSION</c> included, and carries that version back beside each row under
    /// <see cref="BoundedRead.PositionColumn"/>.
    /// <para>
    /// The version bound stays: a bounded pass reads up to the *smaller* of the current end version
    /// and where the row cap stopped it, never past the end version the pass fixed for itself. And
    /// <c>WITH TIES</c> is what makes the stopping point resumable — one key's net change is one row
    /// at one version, so including every row at the boundary version means no key at that version is
    /// left for a next pass that will only look strictly beyond it.
    /// </para>
    /// <para>
    /// The extra column is appended last, after the non-key columns, so
    /// <see cref="FirstKeyOrdinal"/> and the whole ordinal arithmetic below it are unchanged.
    /// </para>
    /// </param>
    public static string BuildIncremental(
        string schema, string table, IReadOnlyList<string> primaryKeyColumns, IReadOnlyList<string> nonKeyColumns,
        Func<string, string>? renderNonKeyColumn = null, bool bounded = false)
    {
        renderNonKeyColumn ??= c => $"base.{SqlIdentifier.Quote(c)}";
        var quotedSchema = SqlIdentifier.Quote(schema);
        var quotedTable = SqlIdentifier.Quote(table);
        var joinCondition = string.Join(" AND ",
            primaryKeyColumns.Select(pk => $"CT.{SqlIdentifier.Quote(pk)} = base.{SqlIdentifier.Quote(pk)}"));

        var selected = new List<string> { "CT.SYS_CHANGE_OPERATION" };

        // Tested against a key column, which is NOT NULL in the source, so NULL here can only mean
        // "the LEFT JOIN matched nothing" and never "this row holds a NULL".
        selected.Add(
            $"CASE WHEN base.{SqlIdentifier.Quote(primaryKeyColumns[0])} IS NULL THEN 1 ELSE 0 END AS {BaseMissingColumn}");

        selected.AddRange(primaryKeyColumns.Select(pk => $"CT.{SqlIdentifier.Quote(pk)}"));
        selected.AddRange(nonKeyColumns.Select(renderNonKeyColumn));

        var limit = "";
        if (bounded)
        {
            var (prefix, _) = MsSqlDialect.Instance.RenderTieSafeRowLimit(BoundedRead.RowLimitParameter);
            limit = prefix;
            selected.Add($"CT.SYS_CHANGE_VERSION AS {SqlIdentifier.Quote(BoundedRead.PositionColumn)}");
        }

        return $"""
            SELECT {limit}{string.Join(", ", selected)}
            FROM CHANGETABLE(CHANGES {quotedSchema}.{quotedTable}, @previousVersion) AS CT
            LEFT JOIN {quotedSchema}.{quotedTable} AS base ON {joinCondition}
            WHERE CT.SYS_CHANGE_VERSION <= @targetVersion
            ORDER BY CT.SYS_CHANGE_VERSION;
            """;
    }
}
