using DbDataSync.Core.Config;
using DbDataSync.Core.Sql;
using DbDataSync.Drivers.Abstractions;

namespace DbDataSync.Drivers.Generic;

/// <summary>
/// The statements the snapshot and SCD Type 2 writers issue. Separate from the writers, like every
/// other statement builder here, so the parts with real defects available in them — what "changed"
/// means, and which version gets closed — are assertable without a database.
/// <para>
/// **Table and subquery aliases carry no <c>AS</c>** — see <c>TriggerAuditStatement</c> for the rule
/// and where it came from: Oracle rejects <c>AS</c> before one outright, though it still requires it
/// before a column alias and before a CTE body. Every alias below is a table alias.
/// </para>
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

        return $"INSERT INTO {quotedTarget} ({columns})\nSELECT {selected}\nFROM {staging}";
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
    /// "changed" check. One use since phase 145: excluding a duplicate key from the bulk statement (a
    /// self-join back onto <paramref name="staging"/> counting how many rows share this row's key),
    /// so the bulk pair only ever reaches a key with exactly one staged row. Phase 132's second use —
    /// scoping the whole statement to one staged row at a time — is gone with the per-row loop it drove.
    /// Null — the default — adds nothing, which is the unmodified statement every pairing but CDC
    /// still gets.
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
        // already uses. Safe as a *scalar* subquery because it can only ever match one row: the only
        // caller that passes one is the bulk statement, whose own stagingFilter excludes every key with
        // more than one staged row (phase 145 moved those onto BuildOpenDuplicateKeyVersions below).
        var validTo = validToExpression is null
            ? dialect.ParameterReference("now")
            : $"(SELECT {validToExpression} FROM {staging} s WHERE {join} AND {changedPredicate})";

        return $"""
            UPDATE {quotedTarget}
            SET {dialect.QuoteIdentifier(HistorizedColumns.ValidTo)} = {validTo},
                {isCurrent} = {dialect.FalseLiteral}
            WHERE {isCurrent} = {dialect.TrueLiteral}
              AND EXISTS (
                SELECT 1 FROM {staging} s
                WHERE {join}
                  AND {changedPredicate}
              )
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
    /// <param name="validFromExpression">
    /// What opens the version — <c>@now</c> by default, the pass-wide time. Phase 132 passes
    /// <c>s.{ChangeOrdering.ChangedAtColumn}</c> instead whenever the staged batch can state one: unlike
    /// <see cref="BuildCloseChanged"/>'s <c>ValidTo</c>, this needs no correlated subquery — the
    /// statement already selects per staged row, so each row simply carries its own value through.
    /// </param>
    /// <param name="stagingFilter">See the identical parameter on <see cref="BuildCloseChanged"/> — the
    /// one remaining use, excluding a duplicate key from the bulk statement.</param>
    public static string BuildOpenVersions(
        SqlDialect dialect,
        string quotedTarget,
        string staging,
        IReadOnlyList<string> keyColumns,
        IReadOnlyList<ColumnMapping> columnMappings,
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

        var versionKeyPrefix = dialect.ParameterReference("versionKeyPrefix");
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
            FROM {staging} s
            WHERE s.{operation} <> 'D'{filterClause}
              AND NOT EXISTS (
                SELECT 1 FROM {quotedTarget} t
                WHERE {openMatch} AND t.{isCurrent} = {dialect.TrueLiteral}
              )
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
            HAVING COUNT(*) > 1
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
        return $"(SELECT COUNT(*) FROM {staging} dup WHERE {match}) = 1";
    }


    /// <summary>
    /// Phase 145's replacement for phase 132's per-key/per-row loop: the derived table both
    /// duplicate-key statements below drive themselves from, computed once over the staged set.
    /// <para>
    /// Three CTEs, each answering one question the C# loop used to answer with a round trip.
    /// <c>__DS_Ordered</c> puts every staged row in its key's own true order and hands it the row
    /// before it (<c>LAG</c>); <c>__DS_Marked</c> keeps only the keys with more than one staged row
    /// (<c>COUNT(*) OVER</c>, which is why finding them no longer needs its own query) and decides,
    /// per row, whether it opens a new version; <c>__DS_Boundary</c> keeps only the rows that actually
    /// change something and hands each one the next such row's timestamp (<c>LEAD</c>), which is that
    /// version's <c>ValidTo</c>.
    /// </para>
    /// <para>
    /// **Why <c>LAG</c> and not a comparison against the target.** The loop's state — "the version
    /// currently open for this key" — collapses to something purely local, and proving that is what
    /// makes the rewrite possible at all: a row either opened its own version or matched the one
    /// already open, so *the previous staged row's values are always the open version's values*. Only
    /// the first row of a key has no predecessor, and that one alone consults the target. A delete
    /// leaves no open version behind, so the row after one always opens, whatever its values.
    /// </para>
    /// <para>
    /// **Why a row can open nothing.** A staged change that touches no mapped column — a CDC row for an
    /// update to a column this mapping does not carry — has the same values as the row before it, so it
    /// opens no version and closes none. Phase 132's loop got this from <c>BuildOpenVersions</c>'s own
    /// <c>NOT EXISTS</c> guard; here it is the <c>LAG</c> comparison, and
    /// <c>Scd2CdcGuaranteedDeliveryIntegrationTests</c>' Id 3 is the scenario that pins it.
    /// </para>
    /// <para>
    /// **Not Oracle-portable, and it does not have to be yet.** Two of the statements built from this
    /// are <c>WITH … UPDATE</c> and <c>WITH … INSERT</c>; Oracle accepts the second and not the first,
    /// because subquery factoring prefixes a <c>SELECT</c> and an <c>UPDATE</c> there has no <c>WITH</c>
    /// clause at all. Unreachable on Oracle today — the duplicate path only runs for a batch carrying
    /// <see cref="ChangeOrdering"/>, which no Oracle reader produces — and this comment is here so that
    /// stops being true loudly rather than quietly.
    /// </para>
    /// <para>
    /// **Why the target lookup excludes this pass's own rows.** The two statements run in sequence
    /// against a target the first one has already written to, and both need the *pre-pass* answer to
    /// "was a version open for this key, with these values." Rows this pass opened are excluded by
    /// surrogate key — a deterministic function of staged data, so the exclusion needs nothing carried
    /// between the statements — which makes the two renderings of this derived table identical no
    /// matter which order they run in. Without it, opening first would make the close see versions it
    /// had just inserted, and closing first would make the open see a key whose version had just been
    /// closed and open a second, spurious copy of it.
    /// </para>
    /// </summary>
    private static string BuildDuplicateKeyCte(
        SqlDialect dialect,
        string quotedTarget,
        string staging,
        IReadOnlyList<string> keyColumns,
        IReadOnlyList<string> valueColumns,
        IReadOnlyList<string> mappedColumns)
    {
        var ordering = dialect.QuoteIdentifier(ChangeOrdering.OrderingColumn);
        var changedAt = dialect.QuoteIdentifier(ChangeOrdering.ChangedAtColumn);
        var operation = dialect.QuoteIdentifier(BatchInsertStagingProvider.OperationColumn);
        var isCurrent = dialect.QuoteIdentifier(HistorizedColumns.IsCurrent);

        var keyPartition = string.Join(", ", keyColumns.Select(k => $"s.{dialect.QuoteIdentifier(k)}"));
        var inKeyOrder = $"PARTITION BY {keyPartition} ORDER BY s.{ordering}";
        var mapped = mappedColumns.Select(dialect.QuoteIdentifier).ToList();

        var orderedSelect = string.Join(",\n        ",
        [
            .. mapped.Select(c => $"s.{c}"),
            $"s.{operation} AS __DS_Op",
            $"s.{ordering} AS __DS_Ordering",
            $"s.{changedAt} AS __DS_ChangedAt",
            $"COUNT(*) OVER (PARTITION BY {keyPartition}) AS __DS_KeyCount",
            $"ROW_NUMBER() OVER ({inKeyOrder}) AS __DS_Rn",
            $"LAG(s.{operation}) OVER ({inKeyOrder}) AS __DS_PrevOp",
            .. valueColumns.Select((c, i) => $"LAG(s.{dialect.QuoteIdentifier(c)}) OVER ({inKeyOrder}) AS __DS_Prev{i}"),
        ]);

        // The same null-safe comparison BuildCloseChanged uses, against two different left-hand sides:
        // the version open before the pass (the first row of a key only), and the row before this one
        // (every other row). A table whose every mapped column is part of the key has nothing that can
        // differ, exactly as it does there.
        var differsFromOpenVersion = valueColumns.Count == 0
            ? "1 = 0"
            : string.Join(" OR ", valueColumns.Select(
                c => NullSafeDiffers($"t.{dialect.QuoteIdentifier(c)}", $"d.{dialect.QuoteIdentifier(c)}")));
        var differsFromPreviousRow = valueColumns.Count == 0
            ? "1 = 0"
            : string.Join(" OR ", valueColumns.Select(
                (c, i) => NullSafeDiffers($"d.__DS_Prev{i}", $"d.{dialect.QuoteIdentifier(c)}")));

        var openVersionMatch = string.Join(" AND ", keyColumns.Select(
            k => $"t.{dialect.QuoteIdentifier(k)} = d.{dialect.QuoteIdentifier(k)}"));
        var boundaryPartition = string.Join(", ", keyColumns.Select(k => $"m.{dialect.QuoteIdentifier(k)}"));
        var inBoundaryOrder = $"PARTITION BY {boundaryPartition} ORDER BY m.__DS_Ordering";
        var boundarySelect = string.Join(", ", mapped.Select(c => $"m.{c}"));
        var notOpenedByThisPass = NotOpenedByThisPass(dialect, staging, "t", keyColumns);

        return $"""
            WITH {OrderedCte} AS (
                SELECT {orderedSelect}
                FROM {staging} s
            ),
            {MarkedCte} AS (
                SELECT d.*,
                    CASE WHEN d.__DS_Op <> 'D' AND (CASE
                        WHEN d.__DS_Rn = 1 THEN
                            CASE WHEN NOT EXISTS (
                                SELECT 1 FROM {quotedTarget} t
                                WHERE {openVersionMatch}
                                  AND t.{isCurrent} = {dialect.TrueLiteral}
                                  AND {notOpenedByThisPass}
                                  AND NOT ({differsFromOpenVersion})
                            ) THEN 1 ELSE 0 END
                        ELSE
                            CASE WHEN d.__DS_PrevOp = 'D' OR ({differsFromPreviousRow}) THEN 1 ELSE 0 END
                        END) = 1
                    THEN 1 ELSE 0 END AS __DS_Opens
                FROM {OrderedCte} d
                WHERE d.__DS_KeyCount > 1
            ),
            {BoundaryCte} AS (
                SELECT {boundarySelect},
                    m.__DS_Op, m.__DS_Ordering, m.__DS_ChangedAt, m.__DS_Opens,
                    LEAD(m.__DS_ChangedAt) OVER ({inBoundaryOrder}) AS __DS_NextChangedAt,
                    ROW_NUMBER() OVER ({inBoundaryOrder}) AS __DS_BoundaryRn
                FROM {MarkedCte} m
                WHERE m.__DS_Opens = 1 OR m.__DS_Op = 'D'
            )
            """;
    }

    private const string OrderedCte = "__DS_Ordered";
    private const string MarkedCte = "__DS_Marked";
    private const string BoundaryCte = "__DS_Boundary";

    /// <summary>
    /// Closes the version a duplicate key already had open, at the moment this pass's first real change
    /// to it happened — one <c>UPDATE</c> for every duplicate key in the batch, where phase 132 issued
    /// one per staged row.
    /// <para>
    /// The moment is the first *boundary* row's timestamp, not the first staged row's: a leading change
    /// that touches no mapped column is not a transition, and closing at it would end a version that
    /// nothing had actually replaced. A key whose staged rows never differ from what is already open
    /// has no boundary at all, matches nothing here, and is left exactly as it was.
    /// </para>
    /// <para>
    /// Runs *after* <see cref="BuildOpenDuplicateKeyVersions"/>, which is why it excludes the rows that
    /// statement just inserted — see <see cref="BuildDuplicateKeyCte"/> on why the order is a free
    /// choice rather than a constraint, and why it still has to be stated.
    /// </para>
    /// </summary>
    public static string BuildCloseDuplicateKeyVersions(
        SqlDialect dialect,
        string quotedTarget,
        string staging,
        IReadOnlyList<string> keyColumns,
        IReadOnlyList<string> valueColumns,
        IReadOnlyList<ColumnMapping> columnMappings)
    {
        var isCurrent = dialect.QuoteIdentifier(HistorizedColumns.IsCurrent);
        var cte = BuildDuplicateKeyCte(
            dialect, quotedTarget, staging, keyColumns, valueColumns,
            columnMappings.Select(m => m.TargetColumn).ToList());
        var firstBoundary = string.Join(" AND ", keyColumns.Select(
            k => $"b.{dialect.QuoteIdentifier(k)} = {quotedTarget}.{dialect.QuoteIdentifier(k)}"))
            + " AND b.__DS_BoundaryRn = 1";

        return $"""
            {cte}
            UPDATE {quotedTarget}
            SET {dialect.QuoteIdentifier(HistorizedColumns.ValidTo)} =
                    (SELECT b.__DS_ChangedAt FROM {BoundaryCte} b WHERE {firstBoundary}),
                {isCurrent} = {dialect.FalseLiteral}
            WHERE {isCurrent} = {dialect.TrueLiteral}
              AND {NotOpenedByThisPass(dialect, staging, quotedTarget, keyColumns)}
              AND EXISTS (
                SELECT 1 FROM {BoundaryCte} b
                WHERE {firstBoundary}
              )
            """;
    }

    /// <summary>
    /// Opens every version a pass owes a duplicate key, in one <c>INSERT</c> — where phase 132 issued
    /// one per staged row of every such key.
    /// <para>
    /// Unlike <see cref="BuildOpenVersions"/> this writes <c>ValidTo</c> too, and does not need the
    /// target to tell it what is open: a version's end is the next boundary row's timestamp, already
    /// computed, and it is the current one exactly when there is no next boundary. So a key whose pass
    /// ends in a delete opens its last version already closed, rather than opening it current and
    /// having a later statement close it again.
    /// </para>
    /// <para>
    /// The surrogate key stays phase 132's <c>{OrderingColumn}|{naturalKey}</c> — unique per row by
    /// construction, which is the whole reason a key with two staged rows stopped colliding — rather
    /// than the pass-wide prefix every singleton key still gets.
    /// </para>
    /// <para>
    /// A delete opens nothing and is excluded here, but it is still in the derived table and still a
    /// boundary: it is what ends the version before it.
    /// </para>
    /// </summary>
    public static string BuildOpenDuplicateKeyVersions(
        SqlDialect dialect,
        string quotedTarget,
        string staging,
        IReadOnlyList<string> keyColumns,
        IReadOnlyList<string> valueColumns,
        IReadOnlyList<ColumnMapping> columnMappings)
    {
        var mappedColumns = columnMappings.Select(m => m.TargetColumn).ToList();
        var cte = BuildDuplicateKeyCte(dialect, quotedTarget, staging, keyColumns, valueColumns, mappedColumns);
        var mapped = mappedColumns.Select(dialect.QuoteIdentifier).ToList();

        var columns = string.Join(", ",
        [
            dialect.QuoteIdentifier(HistorizedColumns.SurrogateKey),
            .. mapped,
            dialect.QuoteIdentifier(HistorizedColumns.ValidFrom),
            dialect.QuoteIdentifier(HistorizedColumns.ValidTo),
            dialect.QuoteIdentifier(HistorizedColumns.IsCurrent),
        ]);

        var selected = string.Join(", ",
        [
            dialect.Concat(["b.__DS_Ordering", "'|'", SurrogateKeySource(dialect, keyColumns, "b")]),
            .. mapped.Select(c => $"b.{c}"),
            "b.__DS_ChangedAt",
            "b.__DS_NextChangedAt",
            $"CASE WHEN b.__DS_NextChangedAt IS NULL THEN {dialect.TrueLiteral} ELSE {dialect.FalseLiteral} END",
        ]);

        return $"""
            {cte}
            INSERT INTO {quotedTarget} ({columns})
            SELECT {selected}
            FROM {BoundaryCte} b
            WHERE b.__DS_Opens = 1
            """;
    }

    /// <summary>
    /// "This target row is not one this pass just opened" — matched on the surrogate key, which for a
    /// duplicate key's version is a pure function of staged data (<c>{OrderingColumn}|{naturalKey}</c>)
    /// and so can be recomputed from the staging table alone, with nothing carried between statements.
    /// See <see cref="BuildDuplicateKeyCte"/> for why both statements need it.
    /// </summary>
    private static string NotOpenedByThisPass(
        SqlDialect dialect, string staging, string targetReference, IReadOnlyList<string> keyColumns)
    {
        var opened = dialect.Concat(
        [
            $"p.{dialect.QuoteIdentifier(ChangeOrdering.OrderingColumn)}",
            "'|'",
            SurrogateKeySource(dialect, keyColumns, "p"),
        ]);
        return $"NOT EXISTS (SELECT 1 FROM {staging} p " +
               $"WHERE {targetReference}.{dialect.QuoteIdentifier(HistorizedColumns.SurrogateKey)} = {opened})";
    }

    /// <summary>
    /// The natural key rendered as text, which the surrogate is derived from. Concatenation rather
    /// than a hash function, because hashing is spelled differently on every engine and this only has
    /// to be unique, not short.
    /// </summary>
    private static string SurrogateKeySource(SqlDialect dialect, IReadOnlyList<string> keyColumns, string alias = "s") =>
        dialect.Concat(
            keyColumns
                .Select(k => dialect.CastToText($"{alias}.{dialect.QuoteIdentifier(k)}"))
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
        return NullSafeDiffers($"{quotedTarget}.{c}", $"s.{c}");
    }

    /// <summary>
    /// The same comparison between any two expressions — phase 145 needs it between a staged row and
    /// the row before it, where neither side is a plain target column.
    /// </summary>
    private static string NullSafeDiffers(string left, string right) =>
        $"(CASE WHEN {left} IS NULL THEN 1 ELSE 0 END <> CASE WHEN {right} IS NULL THEN 1 ELSE 0 END " +
        $"OR ({left} IS NOT NULL AND {right} IS NOT NULL AND {left} <> {right}))";
}
