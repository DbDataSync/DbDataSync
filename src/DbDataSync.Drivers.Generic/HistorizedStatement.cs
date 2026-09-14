using DbDataSync.Core.Config;
using DbDataSync.Core.Sql;
using DbDataSync.Drivers.Abstractions;

namespace DbDataSync.Drivers.Generic;

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
    /// <param name="validToExpression">
    /// What closes the version — <c>@now</c>, the pass-wide time, by default. Phase 132 passes
    /// <c>s.{ChangeOrdering.ChangedAtColumn}</c> instead whenever the staged batch can state a real
    /// per-row source time: a scalar subquery correlated the same way the <c>EXISTS</c> clause already
    /// is, safe because <paramref name="stagingFilter"/> (or, for the unscoped bulk statement, the
    /// caller's own guarantee that a key excluded from the duplicate set has exactly one staged row)
    /// keeps it to at most one matching row.
    /// </param>
    /// <param name="stagingFilter">
    /// An extra condition ANDed onto the staging side, evaluated in the same scope as the join and the
    /// "changed" check. Phase 132 uses this two ways: excluding a duplicate key from the bulk statement
    /// (a self-join back onto <paramref name="staging"/> counting how many rows share this row's key),
    /// and scoping the whole statement to one staged row at a time (<c>s.{OrderingColumn} = @ordering</c>)
    /// when a key has more than one. Null — the default — adds nothing, which is the unmodified
    /// statement every pairing but CDC still gets.
    /// </param>
    public static string BuildCloseChanged(
        SqlDialect dialect,
        string quotedTarget,
        string staging,
        IReadOnlyList<string> keyColumns,
        IReadOnlyList<string> valueColumns,
        string? validToExpression = null,
        string? stagingFilter = null)
    {
        var isCurrent = dialect.QuoteIdentifier(HistorizedColumns.IsCurrent);
        var join = string.Join(" AND ", keyColumns.Select(
            k => $"s.{dialect.QuoteIdentifier(k)} = {quotedTarget}.{dialect.QuoteIdentifier(k)}"));

        var operation = dialect.QuoteIdentifier(BatchInsertStagingProvider.OperationColumn);
        var differs = valueColumns.Count == 0
            // Every column is a key, so nothing can differ — only a delete closes a version.
            ? "1 = 0"
            : string.Join(" OR ", valueColumns.Select(c => NullSafeDiffers(dialect, quotedTarget, c)));

        // The predicate the EXISTS clause already needed, with phase 132's extra staging-side condition
        // folded in when the caller supplies one — unchanged (no trailing AND at all) for every pairing
        // that does not.
        var changedPredicate = stagingFilter is null
            ? $"(s.{operation} = 'D' OR ({differs}))"
            : $"(s.{operation} = 'D' OR ({differs})) AND {stagingFilter}";

        // The unmodified shape when no real per-row time is available: a bound parameter, identical to
        // every pairing before this phase. Only when the caller supplies one does ValidTo need its own
        // correlated scalar subquery — the SET clause has no row alias of its own to read a staged
        // column from, so it re-states the same join and "changed" predicate the EXISTS clause below
        // already uses. Safe as a *scalar* subquery because it can only ever match one row: either
        // stagingFilter itself scopes it to a single staged row (the duplicate-key path), or the caller
        // is the bulk statement, which by construction only ever reaches a key with exactly one.
        var validTo = validToExpression is null
            ? dialect.ParameterReference("now")
            : $"(SELECT {validToExpression} FROM {staging} AS s WHERE {join} AND {changedPredicate})";

        return $"""
            UPDATE {quotedTarget}
            SET {dialect.QuoteIdentifier(HistorizedColumns.ValidTo)} = {validTo},
                {isCurrent} = {dialect.FalseLiteral}
            WHERE {isCurrent} = {dialect.TrueLiteral}
              AND EXISTS (
                SELECT 1 FROM {staging} AS s
                WHERE {join}
                  AND {changedPredicate}
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
    /// <param name="versionKeyPrefixExpression">
    /// What the surrogate key's non-natural-key half is built from — <c>@versionKeyPrefix</c>, the
    /// pass-wide prefix, by default. Phase 132 passes <c>s.{OrderingColumn} + '|'</c> instead when
    /// scoping this statement to one staged row of a key that has more than one: <c>OrderingColumn</c>
    /// is unique per row by construction (LSN and seqval), so two versions of one key opened in the
    /// same pass can no longer collide — the actual fix a duplicate key needs. Every other key keeps
    /// the pass-wide prefix, unchanged.
    /// </param>
    /// <param name="validFromExpression">
    /// What opens the version — <c>@now</c> by default, the pass-wide time. Phase 132 passes
    /// <c>s.{ChangeOrdering.ChangedAtColumn}</c> instead whenever the staged batch can state one: unlike
    /// <see cref="BuildCloseChanged"/>'s <c>ValidTo</c>, this needs no correlated subquery — the
    /// statement already selects per staged row, so each row simply carries its own value through.
    /// </param>
    /// <param name="stagingFilter">See the identical parameter on <see cref="BuildCloseChanged"/> — the
    /// same two uses, duplicate-key exclusion and single-row scoping.</param>
    public static string BuildOpenVersions(
        SqlDialect dialect,
        string quotedTarget,
        string staging,
        IReadOnlyList<string> keyColumns,
        IReadOnlyList<ColumnMapping> columnMappings,
        string? versionKeyPrefixExpression = null,
        string? validFromExpression = null,
        string? stagingFilter = null)
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

        var versionKeyPrefix = versionKeyPrefixExpression ?? dialect.ParameterReference("versionKeyPrefix");
        var validFrom = validFromExpression ?? dialect.ParameterReference("now");

        var selected = string.Join(", ",
        [
            dialect.Concat([versionKeyPrefix, SurrogateKeySource(dialect, keyColumns)]),
            .. mapped.Select(c => $"s.{c}"),
            validFrom,
            dialect.TrueLiteral,
        ]);

        var operation = dialect.QuoteIdentifier(BatchInsertStagingProvider.OperationColumn);
        var openMatch = string.Join(" AND ", keyColumns.Select(
            k => $"t.{dialect.QuoteIdentifier(k)} = s.{dialect.QuoteIdentifier(k)}"));
        var filterClause = stagingFilter is null ? "" : $" AND {stagingFilter}";

        return $"""
            INSERT INTO {quotedTarget} ({columns})
            SELECT {selected}
            FROM {staging} AS s
            WHERE s.{operation} <> 'D'{filterClause}
              AND NOT EXISTS (
                SELECT 1 FROM {quotedTarget} AS t
                WHERE {openMatch} AND t.{isCurrent} = {dialect.TrueLiteral}
              );
            """;
    }

    /// <summary>
    /// The natural keys with more than one row staged in this pass — phase 132's whole reason for
    /// existing. Meaningful only when the staged batch carries <see cref="ChangeOrdering"/> (i.e.
    /// <c>StagedChangeSet.HasChangeOrdering</c>): a pairing that cannot state a true per-row order has
    /// no way to process such a key any better than colliding on it, and no way to detect one either.
    /// <para>
    /// Cheap relative to the pass it belongs to — the staged set is already bounded by the reader's own
    /// row cap, and this is one aggregate scan of it, not a per-row query.
    /// </para>
    /// </summary>
    public static string BuildFindDuplicateKeys(SqlDialect dialect, string staging, IReadOnlyList<string> keyColumns)
    {
        var columns = string.Join(", ", keyColumns.Select(dialect.QuoteIdentifier));
        return $"""
            SELECT {columns}
            FROM {staging}
            GROUP BY {columns}
            HAVING COUNT(*) > 1;
            """;
    }

    /// <summary>
    /// Excludes a duplicate key from the bulk close/open statements — a self-referencing count rather
    /// than a list of literal key values, so the bulk statement's own shape never depends on how many
    /// keys happened to collide this pass, and a composite key never has to be rebuilt as a parameter
    /// list at the call site.
    /// </summary>
    public static string BuildDuplicateKeyExclusion(SqlDialect dialect, string staging, IReadOnlyList<string> keyColumns)
    {
        var match = string.Join(" AND ", keyColumns.Select(
            k => $"dup.{dialect.QuoteIdentifier(k)} = s.{dialect.QuoteIdentifier(k)}"));
        return $"(SELECT COUNT(*) FROM {staging} AS dup WHERE {match}) = 1";
    }

    /// <summary>
    /// The staged rows of one specific key, in true source order — what a duplicate key's own per-row
    /// loop drives itself from. Only <see cref="ChangeOrdering.OrderingColumn"/> is read back: it is
    /// unique per row across the *whole* staged batch, not merely within this key, which is what lets
    /// <c>BuildCloseChanged</c>/<c>BuildOpenVersions</c> scope themselves to exactly one staged row via
    /// <c>s.{OrderingColumn} = @ordering</c> alone.
    /// </summary>
    public static string BuildStagedOrderingsForKey(SqlDialect dialect, string staging, IReadOnlyList<string> keyColumns)
    {
        var ordering = dialect.QuoteIdentifier(ChangeOrdering.OrderingColumn);
        var match = string.Join(" AND ", keyColumns.Select(
            (k, i) => $"{dialect.QuoteIdentifier(k)} = {dialect.ParameterReference($"key{i}")}"));
        return $"""
            SELECT {ordering}
            FROM {staging}
            WHERE {match}
            ORDER BY {ordering};
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
    /// <summary>
    /// "One of them is null and the other is not, or neither is and they differ."
    /// <para>
    /// The nullness halves go through <c>CASE</c> rather than being compared directly. <c>IS NULL</c>
    /// is a *predicate*, not a value, so <c>(a IS NULL) &lt;&gt; (b IS NULL)</c> is a syntax error on
    /// SQL Server — which nothing found until phase 68 ran this writer against a real server for the
    /// first time. <c>CASE</c> is the portable way to get a comparable value out of a predicate, and it
    /// says the same thing on Postgres, which had accepted the original.
    /// </para>
    /// </summary>
    private static string NullSafeDiffers(SqlDialect dialect, string quotedTarget, string column)
    {
        var c = dialect.QuoteIdentifier(column);
        return $"(CASE WHEN {quotedTarget}.{c} IS NULL THEN 1 ELSE 0 END <> CASE WHEN s.{c} IS NULL THEN 1 ELSE 0 END " +
               $"OR ({quotedTarget}.{c} IS NOT NULL AND s.{c} IS NOT NULL AND {quotedTarget}.{c} <> s.{c}))";
    }
}
