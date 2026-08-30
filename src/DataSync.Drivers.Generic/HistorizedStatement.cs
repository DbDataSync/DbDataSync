using DataSync.Core.Config;

namespace DataSync.Drivers.Generic;

/// <summary>
/// The statements the snapshot and SCD Type 2 writers issue. Separate from the writers, like every
/// other statement builder here, so the parts with real defects available in them — what "changed"
/// means, and which version gets closed — are assertable without a database.
/// </summary>
public static class HistorizedStatement
{
    /// <summary>
    /// Appends every staged row, marked with when the pass ran. No <c>WHERE</c>, no join, nothing
    /// compared — see <c>SnapshotWriter</c> for why that is the point rather than an omission.
    /// </summary>
    public static string BuildSnapshotInsert(
        SqlDialect dialect, string quotedTarget, IReadOnlyList<ColumnMapping> columnMappings, string staging)
    {
        var mapped = columnMappings.Select(m => dialect.QuoteIdentifier(m.TargetColumn)).ToList();
        var columns = string.Join(", ", [.. mapped, dialect.QuoteIdentifier(HistorizedColumns.SnapshotAt)]);
        var selected = string.Join(", ", [.. mapped, dialect.ParameterReference("snapshotAt")]);

        return $"INSERT INTO {quotedTarget} ({columns})\nSELECT {selected}\nFROM {staging};";
    }

    /// <summary>
    /// Closes the open version of every key whose staged row differs from it, or which the source
    /// deleted.
    ///
    /// <para>
    /// **The comparison is column by column, and null-safe.** <c>t.c &lt;&gt; s.c</c> is *unknown*
    /// when either side is null, so a value becoming null — or arriving where there was none — would
    /// not register as a change and the version would never close. That is a silent wrong answer, and
    /// the reason each column is compared with an explicit null-handling pair rather than with
    /// <c>&lt;&gt;</c>.
    /// </para>
    ///
    /// <para>
    /// A row hash would be cheaper on a wide table and is the obvious next step; it is not the first
    /// cut, because a hash makes "which column changed" unanswerable while this is still being
    /// trusted.
    /// </para>
    /// </summary>
    public static string BuildCloseChanged(
        SqlDialect dialect,
        string quotedTarget,
        string staging,
        IReadOnlyList<string> keyColumns,
        IReadOnlyList<string> valueColumns)
    {
        var isCurrent = dialect.QuoteIdentifier(HistorizedColumns.IsCurrent);
        var join = string.Join(" AND ", keyColumns.Select(
            k => $"s.{dialect.QuoteIdentifier(k)} = {quotedTarget}.{dialect.QuoteIdentifier(k)}"));

        var operation = dialect.QuoteIdentifier(BatchInsertStagingProvider.OperationColumn);
        var differs = valueColumns.Count == 0
            // Every column is a key, so nothing can differ — only a delete closes a version.
            ? "1 = 0"
            : string.Join(" OR ", valueColumns.Select(c => NullSafeDiffers(dialect, quotedTarget, c)));

        return $"""
            UPDATE {quotedTarget}
            SET {dialect.QuoteIdentifier(HistorizedColumns.ValidTo)} = {dialect.ParameterReference("now")},
                {isCurrent} = {dialect.FalseLiteral}
            WHERE {isCurrent} = {dialect.TrueLiteral}
              AND EXISTS (
                SELECT 1 FROM {staging} AS s
                WHERE {join}
                  AND (s.{operation} = 'D' OR ({differs}))
              );
            """;
    }

    /// <summary>
    /// Opens a version for every staged row that is not a delete and whose key has no open version —
    /// which after <see cref="BuildCloseChanged"/> is exactly the new keys and the ones just closed.
    /// <para>
    /// The surrogate key is computed here rather than read back from the target: a deterministic value
    /// over the natural key and the version's start, so <c>ApplyAsync</c>'s signature is unchanged and
    /// nothing has to round-trip an identity column.
    /// </para>
    /// </summary>
    public static string BuildOpenVersions(
        SqlDialect dialect,
        string quotedTarget,
        string staging,
        IReadOnlyList<string> keyColumns,
        IReadOnlyList<ColumnMapping> columnMappings)
    {
        var isCurrent = dialect.QuoteIdentifier(HistorizedColumns.IsCurrent);
        var mapped = columnMappings.Select(m => dialect.QuoteIdentifier(m.TargetColumn)).ToList();

        var columns = string.Join(", ",
        [
            dialect.QuoteIdentifier(HistorizedColumns.SurrogateKey),
            .. mapped,
            dialect.QuoteIdentifier(HistorizedColumns.ValidFrom),
            isCurrent,
        ]);

        var selected = string.Join(", ",
        [
            dialect.Concat([dialect.ParameterReference("versionKeyPrefix"), SurrogateKeySource(dialect, keyColumns)]),
            .. mapped.Select(c => $"s.{c}"),
            dialect.ParameterReference("now"),
            dialect.TrueLiteral,
        ]);

        var operation = dialect.QuoteIdentifier(BatchInsertStagingProvider.OperationColumn);
        var openMatch = string.Join(" AND ", keyColumns.Select(
            k => $"t.{dialect.QuoteIdentifier(k)} = s.{dialect.QuoteIdentifier(k)}"));

        return $"""
            INSERT INTO {quotedTarget} ({columns})
            SELECT {selected}
            FROM {staging} AS s
            WHERE s.{operation} <> 'D'
              AND NOT EXISTS (
                SELECT 1 FROM {quotedTarget} AS t
                WHERE {openMatch} AND t.{isCurrent} = {dialect.TrueLiteral}
              );
            """;
    }

    /// <summary>
    /// The natural key rendered as text, which the surrogate is derived from. Concatenation rather
    /// than a hash function, because hashing is spelled differently on every engine and this only has
    /// to be unique, not short.
    /// </summary>
    private static string SurrogateKeySource(SqlDialect dialect, IReadOnlyList<string> keyColumns) =>
        dialect.Concat(
            keyColumns
                .Select(k => dialect.CastToText($"s.{dialect.QuoteIdentifier(k)}"))
                .SelectMany((part, i) => i == 0 ? [part] : new[] { "'|'", part }));

    /// <summary>
    /// <c>a &lt;&gt; b</c> is *unknown* when either side is null, so a plain comparison silently
    /// misses a value that became null and one that arrived where there was none. Both are changes, and
    /// a version that never closes because of them is a target quietly reporting stale data as current.
    /// </summary>
    private static string NullSafeDiffers(SqlDialect dialect, string quotedTarget, string column)
    {
        var c = dialect.QuoteIdentifier(column);
        return $"(({quotedTarget}.{c} IS NULL) <> (s.{c} IS NULL) " +
               $"OR ({quotedTarget}.{c} IS NOT NULL AND s.{c} IS NOT NULL AND {quotedTarget}.{c} <> s.{c}))";
    }
}
