using DataSync.Core.Config;

namespace DataSync.Drivers.Abstractions;

/// <summary>
/// How a reader caps one pass by row count instead of letting the change window decide how big the
/// pass is.
/// <para>
/// **The trap this exists to avoid.** A naive cap — <c>TOP n</c> on the row read, with the watermark
/// still taken from the source's true current max — silently drops every row past the nth and then
/// records a position that says they were read. Those rows are never seen again. So a bounded read is
/// not "the same read, smaller": the position it reports has to be one it genuinely reached, and no
/// two rows sharing that position may be split across passes. Both engines in scope express exactly
/// that in one clause — <c>TOP (n) WITH TIES</c>, <c>FETCH FIRST n ROWS WITH TIES</c> — which is why
/// this is a clause and not a three-step boundary negotiation.
/// </para>
/// <para>
/// Only the two reference readers implement it (plain Watermark, SQL Server Change Tracking). CDC and
/// Postgres logical replication have their own boundary semantics — an LSN can cover a whole
/// transaction's worth of rows — and are real follow-on work rather than the same technique assumed to
/// port. See <c>architecture/planning/done/chunked-apply-and-bounded-reads.md</c>.
/// </para>
/// </summary>
public static class BoundedRead
{
    /// <summary>Option key, so the readers, the descriptor and the tests agree on one spelling.</summary>
    public const string OptionName = "maxRowsPerRead";

    /// <summary>Statement parameter the row cap binds to.</summary>
    public const string RowLimitParameter = "maxRows";

    /// <summary>
    /// Alias for the ordering column a bounded read carries back beside the row, so the position
    /// actually reached can be read off the last row rather than asked for in a second query.
    /// <para>
    /// Needed because the ordering column is frequently *not* in the projection — a mapping that does
    /// not carry its own watermark column is perfectly ordinary — so a bounded read has to select it
    /// explicitly. It is appended last, after everything the schema describes, which is what keeps
    /// every existing ordinal untouched.
    /// </para>
    /// </summary>
    public const string PositionColumn = "__DS_Position";

    /// <summary>Declared once and reused, so a reader offering this setting describes it identically.</summary>
    public static ParameterDescriptor Descriptor { get; } = new()
    {
        Name = OptionName,
        Label = "Max rows per read",
        Description =
            "Caps how many rows one pass reads, so a large backlog is worked through over several " +
            "passes instead of one enormous one. The pass then records the position it actually " +
            "reached rather than the source's current position, and the next pass continues from " +
            "there. Rows sharing the boundary value are never split across passes. Leave empty, or " +
            "set 0, to read the whole change window in one pass.",
        Type = ParameterType.Number,
    };

    /// <summary>
    /// The configured cap, or null for "read the whole window".
    /// <para>
    /// Unbounded by default, unlike <see cref="ApplyBatch"/>. Bounding is not a free performance win
    /// the way a smaller write statement is: it changes how many passes a catch-up takes, and it is
    /// only cheap if the ordering column is indexed — on a table where it is not, ordering the whole
    /// window to take the first n of it is work the unbounded read was not doing. That is an operator's
    /// call about their own table, so it stays off until one is made.
    /// </para>
    /// </summary>
    public static int? Read(IReadOnlyDictionary<string, string> options)
    {
        if (!options.TryGetValue(OptionName, out var raw) || string.IsNullOrWhiteSpace(raw))
            return null;

        return int.TryParse(raw.Trim(), out var max) && max > 0 ? max : null;
    }
}

/// <summary>
/// Where a bounded read got to, filled in while its rows stream and read once they are drained.
/// <para>
/// Deliberately mutable, for the same reason <see cref="ReadDiagnostics"/> is: the value cannot exist
/// before the read happens. An unbounded read computes its next position up front and has no use for
/// this; a bounded one *is* its rows, and the last of them is the answer.
/// </para>
/// </summary>
public sealed class BoundedReadPosition
{
    /// <summary>
    /// The position to store instead of the one computed up front, or null to keep that one.
    /// <para>
    /// Set by the reader, on the reader's own rules, because "how far did this pass get" does not mean
    /// the same thing to every mechanism. A watermark scan has no other answer than its last row, so it
    /// always sets this once it has read anything. Change Tracking does: a pass that drained its whole
    /// window should advance to the window's end rather than to its last row, or an idle table's
    /// position would never move and would eventually fall off the back of its own change log — so that
    /// reader sets this only when the cap actually cut the read short.
    /// </para>
    /// </summary>
    public string? Reached { get; set; }
}
