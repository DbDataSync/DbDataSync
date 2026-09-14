using DbDataSync.Core.Sql;
using DbDataSync.Drivers.Abstractions;

namespace DbDataSync.Drivers.MsSql;

/// <summary>
/// Builds the SQL Server CDC read. Separated from the reader, like
/// <see cref="MsSqlChangeTrackingStatement"/>, so the select list and the boundary arithmetic — the
/// two parts with real defects available in them — can be asserted without a live SQL Server.
/// </summary>
public static class MsSqlCdcStatement
{
    /// <summary>The change-table columns every CDC function returns ahead of the source's own.</summary>
    public const string OperationColumn = "__$operation";

    /// <summary>The commit position of the transaction a change belongs to. Every row of one
    /// transaction shares it, which is what makes it the only tie-safe boundary CDC has.</summary>
    public const string StartLsnColumn = "__$start_lsn";

    /// <summary>Orders changes within one <see cref="StartLsnColumn"/>. Only all-changes returns it —
    /// net changes has already collapsed the transaction it would order inside.</summary>
    public const string SeqvalColumn = "__$seqval";

    /// <summary>Fixed leading ordinal: the operation, then the mapped columns.</summary>
    public const int OperationOrdinal = 0;

    /// <summary>
    /// Where a bounded read's position column lands: last, after the mapped columns, so every ordinal
    /// the reader already uses is untouched by turning the cap on.
    /// </summary>
    public static int PositionOrdinal(int columnCount) => columnCount + 1;

    /// <summary>
    /// The two shapes CDC offers, and the reason the reader prefers one.
    /// <para>
    /// <c>fn_cdc_get_net_changes_*</c> collapses a key's changes within the window to a single row —
    /// the semantics Change Tracking has, and the ones a mirror wants: a row updated fifty times
    /// between passes is one row to write, not fifty. It exists only when the capture instance was
    /// created with <c>@supports_net_changes = 1</c>, which needs a primary key.
    /// </para>
    /// <para>
    /// <c>fn_cdc_get_all_changes_*</c> is always there and reports every intermediate change. It is
    /// the fallback, and the reader logs when it is used, because a pass reading fifty rows to write
    /// one is a thing an operator should be able to find out about.
    /// </para>
    /// </summary>
    public enum CdcFunction
    {
        NetChanges,
        AllChanges,
    }

    /// <summary>
    /// The read.
    /// <para>
    /// <c>sys.fn_cdc_increment_lsn</c> on the lower bound is not a nicety. CDC's window functions are
    /// **inclusive of <c>@from</c>**, so passing the stored LSN unchanged re-reads the last pass's
    /// final change on every pass. It is harmless to the data — writers upsert — and it makes every
    /// pass look like it found work, which is exactly how a monitoring graph starts lying.
    /// </para>
    /// </summary>
    /// <param name="captureInstance">
    /// The capture instance, not the table: CDC names its functions after the instance
    /// (<c>dbo_Orders</c> by default), a table can have two, and they can be named anything. Composed
    /// into the function name because a function name is not something SQL Server lets you
    /// parameterise — which is why <see cref="MsSqlCdcCatalog"/> reads it from the catalog rather than
    /// accepting it from config.
    /// </param>
    /// <param name="bounded">
    /// Caps the pass at <see cref="BoundedRead.RowLimitParameter"/> rows and carries the position it
    /// reached back beside each row under <see cref="BoundedRead.PositionColumn"/>.
    /// <para>
    /// **The tie is on <c>__$start_lsn</c> alone, and that is the whole correctness argument.** A CDC
    /// watermark is an LSN — <c>MsSqlCdcCatalog.ToWatermark</c> stores one, and the next pass resumes at
    /// <c>fn_cdc_increment_lsn</c> of it — so an LSN is the finest position this reader can *record*.
    /// Tying on <c>(__$start_lsn, __$seqval)</c>, which is what the ordering suggests, would let a pass
    /// stop halfway through a transaction, record that transaction's LSN, and have the next pass start
    /// strictly beyond it: every remaining row of that transaction silently lost. Tying on the LSN
    /// instead means <c>WITH TIES</c> pulls in the rest of the transaction whatever the cap said, so the
    /// position reached is always one no row still sits at.
    /// </para>
    /// <para>
    /// The consequence is deliberate and worth stating: a single transaction larger than the cap is
    /// delivered whole, in one pass, over the cap. The cap bounds the common case rather than
    /// guaranteeing a ceiling, because the alternative is losing rows.
    /// </para>
    /// <para>
    /// Ordering *within* the boundary still needs <c>__$seqval</c>, which cannot share one <c>ORDER
    /// BY</c> with a tie on the LSN alone — so the cap is taken in a derived table ordered by the LSN
    /// and the rows come out of it ordered by both. Two orderings because they answer two different
    /// questions: where may this pass stop, and in what order did these changes happen.
    /// </para>
    /// </param>
    /// <param name="inclusiveFloor">
    /// <c>ChangesFromEarliest</c>'s one difference from the ordinary incremental read: <c>@storedLsn</c>
    /// is the feed's surviving floor (<c>min_lsn</c>) itself, read inclusively, rather than a stored
    /// position to resume strictly after. A genuinely different statement — no
    /// <c>fn_cdc_increment_lsn</c> at all — not a decremented bound, which would put back the exact
    /// problem this design exists to solve: there is no LSN below <c>min_lsn</c> to decrement to, and
    /// this reader's own <c>Compare(storedLsn, minLsn) &lt; 0</c> guard exists precisely to refuse one
    /// that claims there is.
    /// </param>
    public static string BuildRead(
        string captureInstance, CdcFunction function, IReadOnlyList<string> columns,
        Func<string, string>? renderColumn = null, bool bounded = false, bool inclusiveFloor = false)
    {
        renderColumn ??= c => SqlIdentifier.Quote(c);
        var from = inclusiveFloor ? "@storedLsn" : "sys.fn_cdc_increment_lsn(@storedLsn)";

        var name = function == CdcFunction.NetChanges
            ? $"cdc.fn_cdc_get_net_changes_{captureInstance}"
            : $"cdc.fn_cdc_get_all_changes_{captureInstance}";

        // The two functions do not return the same bookkeeping columns: __$seqval orders changes
        // *within* a transaction and only all-changes has it, because net changes has already
        // collapsed them. Ordering by it unconditionally is an "Invalid column name" on the mode this
        // reader prefers — found by the integration tests, and not by reading the documentation.
        var hasSeqval = function == CdcFunction.AllChanges;

        var selected = new List<string> { OperationColumn };
        selected.AddRange(columns.Select(renderColumn));
        // Phase 132: a per-row source order and a per-row source time, appended after the mapped
        // columns and before any bounded position column, so the reader's ordinal math for the mapped
        // columns is untouched and the position column (which stays out-of-band) is still last. Both
        // CDC functions expose __$start_lsn; only all-changes also has __$seqval, since net changes has
        // already collapsed the transaction it would order inside — the same "Invalid column name" trap
        // the existing ORDER BY already avoids, avoided here the same way: a literal zero stands in for
        // the missing seqval half rather than referencing a column that mode does not return.
        var seqvalHex = hasSeqval
            ? $"CONVERT(varchar(20), ISNULL({SeqvalColumn}, 0x00000000000000000000), 2)"
            : "CONVERT(varchar(20), 0x00000000000000000000, 2)";
        var changeOrdering =
            $"CONVERT(varchar(20), {StartLsnColumn}, 2) + {seqvalHex} AS {SqlIdentifier.Quote(ChangeOrdering.OrderingColumn)}";
        var changedAt = $"sys.fn_cdc_map_lsn_to_time({StartLsnColumn}) AS {SqlIdentifier.Quote(ChangeOrdering.ChangedAtColumn)}";
        selected.Add(changeOrdering);
        selected.Add(changedAt);

        // 'all' for net changes means "give me the net row and tell me the operation"; 'all' for all
        // changes means "every change, without before-images". Same literal, and both are what this
        // reader wants — before-images are phase 32's explicit non-goal.
        if (!bounded)
        {
            return $"""
                DECLARE @from binary(10) = {from};
                SELECT {string.Join(", ", selected)}
                FROM {name}(@from, @toLsn, N'all')
                ORDER BY {(hasSeqval ? $"{StartLsnColumn}, {SeqvalColumn}" : StartLsnColumn)};
                """;
        }

        var position = SqlIdentifier.Quote(BoundedRead.PositionColumn);
        var (limit, _) = MsSqlDialect.Instance.RenderTieSafeRowLimit(BoundedRead.RowLimitParameter);

        // The derived table carries __$seqval only so the outer ORDER BY can use it; it is not
        // projected outwards, which is what keeps the bounded result set the unbounded one plus a
        // position column and leaves every ordinal the reader reads by unchanged.
        var inner = new List<string>(selected);
        if (hasSeqval)
            inner.Add(SeqvalColumn);
        inner.Add($"{StartLsnColumn} AS {position}");

        var outer = new List<string> { OperationColumn };
        outer.AddRange(columns.Select(SqlIdentifier.Quote));
        outer.Add(SqlIdentifier.Quote(ChangeOrdering.OrderingColumn));
        outer.Add(SqlIdentifier.Quote(ChangeOrdering.ChangedAtColumn));
        outer.Add(position);

        return $"""
            DECLARE @from binary(10) = {from};
            SELECT {string.Join(", ", outer)}
            FROM (
                SELECT {limit}{string.Join(", ", inner)}
                FROM {name}(@from, @toLsn, N'all')
                ORDER BY {StartLsnColumn}
            ) AS capped
            ORDER BY {(hasSeqval ? $"{position}, {SeqvalColumn}" : position)};
            """;
    }

    /// <summary>
    /// The first pass, before any position exists: every row, as an insert — the same shape Change
    /// Tracking's first pass takes, and for the same reason. CDC's change table only holds what has
    /// happened since capture was enabled, so a table that already had rows would otherwise start
    /// half-replicated with nothing to say so.
    /// </summary>
    public static string BuildFullLoad(string schema, string table, string projection, string? filter) =>
        MsSqlChangeTrackingStatement.BuildFullLoad(schema, table, projection, filter);

    /// <summary>
    /// <c>__$operation</c>: 1 delete, 2 insert, 3 update-before, 4 update-after.
    /// <para>
    /// 3 only appears with <c>'all update old'</c>, which this reader does not ask for — so seeing one
    /// means the statement was not the one built here, and guessing at it would be worse than saying
    /// so.
    /// </para>
    /// </summary>
    public static ChangeOperationCode Operation(int code) => code switch
    {
        1 => ChangeOperationCode.Delete,
        2 => ChangeOperationCode.Insert,
        4 => ChangeOperationCode.Update,
        3 => throw new InvalidOperationException(
            "CDC returned an update-before image (__$operation = 3), which this reader never requests. " +
            "The statement that produced it was not the one this reader builds."),
        _ => throw new InvalidOperationException($"Unknown CDC __$operation value '{code}'."),
    };

    public enum ChangeOperationCode
    {
        Insert,
        Update,
        Delete,
    }
}
