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
/// Three readers implement it: plain Watermark and SQL Server Change Tracking, where the boundary is a
/// single ordering column, and SQL Server CDC, where it is not. An LSN covers a whole transaction's
/// worth of rows, so CDC ties on <c>__$start_lsn</c> alone while ordering by
/// <c>(__$start_lsn, __$seqval)</c> — the boundary has to be a position the stored watermark can
/// actually express, and a CDC watermark is an LSN. See <c>MsSqlCdcStatement.BuildRead</c> and
/// <c>architecture/implementation/done/phase-084-cdc-row-bounded-reads.md</c>. Postgres logical
/// replication remains follow-on work.
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

    /// <summary>
    /// Rows per pass for a reader that caps by default — the log-based ones, whose ordering column is
    /// the change table's own clustered key and therefore free to order by.
    /// <para>
    /// 50,000 because the cap is a limit on a whole pass, not on a query: every row it admits is read,
    /// staged and then applied in <see cref="ApplyBatch.DefaultSize"/>-row chunks, so this is ten apply
    /// chunks' worth of work and one staging table's worth of bulk copy. Small enough that a pass
    /// finishes well inside phase 76's 1800s default command timeout on any source worth replicating,
    /// and that a worker slot turns over often enough for a sibling mapping to get one; large enough
    /// that a real backlog is not drained in thousand-row sips, each paying for its own catalog
    /// lookups, staging table and watermark write. Chosen against the whole pipeline rather than
    /// against the read, because a number that only the SELECT can live with is the easy mistake here.
    /// </para>
    /// </summary>
    public const int DefaultMaxRows = 50_000;

    /// <summary>
    /// Declared once and reused, so a reader offering this setting describes it identically. This is
    /// the opt-in wording, for readers where <see cref="Read(IReadOnlyDictionary{string,string})"/>
    /// leaves the cap off unless configured.
    /// </summary>
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
    /// The same setting as <see cref="Descriptor"/>, worded for a reader that caps by default. Shared
    /// by the two log-based readers rather than declared twice, which is what gives CDC the SPA's
    /// parameter surface without a line of frontend work: the UI renders whatever
    /// <c>IChangeReader.Parameters</c> declares.
    /// </summary>
    public static ParameterDescriptor CappedDescriptor { get; } = new()
    {
        Name = OptionName,
        Label = "Max rows per read",
        Description =
            "Caps how many rows one pass reads, so a large backlog is worked through over several " +
            "passes instead of one enormous one. The pass then records the position it actually " +
            "reached rather than the source's current position, and the next pass continues from " +
            $"there. Rows sharing the boundary position are never split across passes. Defaults to " +
            $"{DefaultMaxRows}; set 0 to read the whole change window in one pass.",
        Type = ParameterType.Number,
        Default = DefaultMaxRows.ToString(),
    };

    /// <summary>
    /// The configured cap, or null for "read the whole window" — for a reader that stays unbounded
    /// until an operator says otherwise.
    /// <para>
    /// Bounding is not a free performance win the way a smaller write statement is: it changes how many
    /// passes a catch-up takes, and it is only cheap if the ordering column is indexed — on a table
    /// where it is not, ordering the whole window to take the first n of it is work the unbounded read
    /// was not doing. For a watermark scan over a column the operator chose, that is their call about
    /// their own table, so it stays off until one is made. A log-based reader has no such doubt: its
    /// ordering column is the change table's clustered key, which is why those readers pass
    /// <see cref="DefaultMaxRows"/> to the overload below instead.
    /// </para>
    /// </summary>
    public static int? Read(IReadOnlyDictionary<string, string> options) => Read(options, whenUnset: null);

    /// <summary>
    /// The configured cap, falling back to <paramref name="whenUnset"/> when the option says nothing
    /// usable. Zero, and only zero, is an operator asking for the unbounded read back.
    /// <para>
    /// The fallback is a parameter rather than a constant because the two answers are both right:
    /// off for a scan over an arbitrary column, on for a change table. Making each reader name its own
    /// keeps that decision at the call site where the reasoning for it lives, instead of in a default
    /// that silently applies to whichever reader is added next.
    /// </para>
    /// <para>
    /// An unparseable or negative value falls back rather than throwing, exactly as
    /// <see cref="ApplyBatch.Read"/> does: this is a performance knob, and failing a run over a typo in
    /// one would be a worse outcome than running it at a sensible size.
    /// </para>
    /// </summary>
    public static int? Read(IReadOnlyDictionary<string, string> options, int? whenUnset)
    {
        if (!options.TryGetValue(OptionName, out var raw) || string.IsNullOrWhiteSpace(raw))
            return whenUnset;

        if (!int.TryParse(raw.Trim(), out var max) || max < 0)
            return whenUnset;

        return max == 0 ? null : max;
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
