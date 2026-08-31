using DataSync.Core.Sql;

namespace DataSync.Drivers.Generic;

/// <summary>
/// Builds the reads against a trigger-maintained shadow table. Separate from the reader, like
/// <c>MsSqlChangeTrackingStatement</c>, because the select list and the collapsing are the parts with
/// real defects available in them and both are assertable without a database.
/// <para>
/// Engine-neutral apart from quoting and placeholders, which is the whole argument for this mechanism:
/// one reader serves every engine that has triggers, and only the <c>CREATE TRIGGER</c> DDL diverges.
/// </para>
/// </summary>
public static class TriggerAuditStatement
{
    /// <summary>Monotonic by construction, and the watermark. Never the key, and never a timestamp:
    /// two rows changed in the same tick are two positions.</summary>
    public const string SequenceColumn = "DS_Seq";

    /// <summary><c>I</c>, <c>U</c> or <c>D</c>, written by the trigger.</summary>
    public const string OperationColumn = "DS_Op";

    /// <summary>Diagnostic only. Nothing reads it to decide anything — a timestamp cannot order a
    /// change feed, which is the mistake it exists to invite.</summary>
    public const string ChangedAtColumn = "DS_ChangedAt";

    /// <summary>Computed marker, not a stored column: 1 when the LEFT JOIN found no base row.</summary>
    public const string BaseMissingColumn = "DS_BaseMissing";

    /// <summary>Fixed leading ordinals, ahead of the key columns — the same shape the Change Tracking
    /// reader uses, so the two readers' row-building code reads the same way.</summary>
    public const int OperationOrdinal = 0;
    public const int BaseMissingOrdinal = 1;
    public const int FirstKeyOrdinal = 2;

    /// <summary>
    /// Beside the source table, in its own schema, because that is where an operator will look for it
    /// and where the rights that let them create a trigger already reach. A separate schema is tidier
    /// and needs a grant they may not have.
    /// </summary>
    public static string ShadowTableName(string table) => $"DS_Changes_{table}";

    public static string BuildMaxSequence(SqlDialect dialect, string schema, string table) =>
        $"SELECT MAX({dialect.QuoteIdentifier(SequenceColumn)}) " +
        $"FROM {dialect.QualifyTable(schema, ShadowTableName(table))};";

    /// <summary>
    /// The incremental read.
    ///
    /// <para>
    /// **Collapsed to the net change per key.** A shadow table records every write, so a row updated
    /// fifty times between passes is fifty rows — and staging fifty versions of one row to write the
    /// last one makes this mechanism unusable on a busy table. The derived table takes the highest
    /// sequence per key inside the window and joins back to it, which is what <c>CHANGETABLE</c> does
    /// natively and what a shadow table has to be told to do.
    /// </para>
    ///
    /// <para>
    /// **The key comes from the shadow row and never from the base row.** Phase 12 found exactly this
    /// bug in the Change Tracking reader: the select list re-emitted the key from <c>base.*</c>, and
    /// for a row deleted since capture the LEFT JOIN made it NULL — destroying the one value still
    /// reliable and turning a skippable row into a failed run. The shadow row always has the key; the
    /// base row may not exist at all.
    /// </para>
    /// </summary>
    /// <param name="renderNonKeyColumn">
    /// How each non-key column is written — plain <c>base.[Col]</c> by default, or a transform
    /// expression aliased back to the column's own name. A function rather than a list, so this
    /// method's ordinal arithmetic (and therefore <see cref="BaseMissingOrdinal"/>) is unaffected by
    /// what a mapping does.
    /// </param>
    public static string BuildRead(
        SqlDialect dialect,
        string schema,
        string table,
        IReadOnlyList<string> keyColumns,
        IReadOnlyList<string> nonKeyColumns,
        Func<string, string>? renderNonKeyColumn = null)
    {
        if (keyColumns.Count == 0)
            throw new InvalidOperationException(
                "A trigger-audit shadow table is keyed by the source's primary key columns, and this " +
                "table has none. Without a key there is nothing to collapse changes by and nothing to " +
                "identify a deleted row with.");

        renderNonKeyColumn ??= c => $"base.{dialect.QuoteIdentifier(c)}";

        var shadow = dialect.QualifyTable(schema, ShadowTableName(table));
        var baseTable = dialect.QualifyTable(schema, table);
        var seq = dialect.QuoteIdentifier(SequenceColumn);
        var previous = dialect.ParameterReference("previousSequence");
        var target = dialect.ParameterReference("targetSequence");

        var keyList = string.Join(", ", keyColumns.Select(dialect.QuoteIdentifier));
        var joinToBase = string.Join(" AND ", keyColumns.Select(
            k => $"c.{dialect.QuoteIdentifier(k)} = base.{dialect.QuoteIdentifier(k)}"));

        var selected = new List<string> { $"c.{dialect.QuoteIdentifier(OperationColumn)}" };

        // Tested against a key column, which cannot be null in the shadow row, so NULL here can only
        // mean the LEFT JOIN matched nothing — never that the base row holds a NULL.
        selected.Add(
            $"CASE WHEN base.{dialect.QuoteIdentifier(keyColumns[0])} IS NULL THEN 1 ELSE 0 END " +
            $"AS {dialect.QuoteIdentifier(BaseMissingColumn)}");

        selected.AddRange(keyColumns.Select(k => $"c.{dialect.QuoteIdentifier(k)}"));
        selected.AddRange(nonKeyColumns.Select(renderNonKeyColumn));

        return $"""
            SELECT {string.Join(", ", selected)}
            FROM {shadow} AS c
            JOIN (
                SELECT {keyList}, MAX({seq}) AS {seq}
                FROM {shadow}
                WHERE {seq} > {previous} AND {seq} <= {target}
                GROUP BY {keyList}
            ) AS latest ON c.{seq} = latest.{seq}
            LEFT JOIN {baseTable} AS base ON {joinToBase}
            ORDER BY c.{seq};
            """;
    }

    /// <summary>
    /// Deletes the history this replication has finished with, called from the reader's
    /// acknowledgement — see <c>IPositionAcknowledging</c>. Strictly below the acknowledged position,
    /// never at or above it: the row *at* the watermark is the one a re-read would start after, and
    /// deleting it costs nothing and proves nothing.
    /// </summary>
    public static string BuildPrune(SqlDialect dialect, string schema, string table) =>
        $"DELETE FROM {dialect.QualifyTable(schema, ShadowTableName(table))} " +
        $"WHERE {dialect.QuoteIdentifier(SequenceColumn)} <= {dialect.ParameterReference("throughSequence")};";
}
