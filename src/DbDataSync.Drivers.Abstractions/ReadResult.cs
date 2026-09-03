namespace DbDataSync.Drivers.Abstractions;

/// <summary>
/// <paramref name="NewWatermark"/> is computed by the reader up front (e.g. the Change Tracking
/// version to read up to) before <paramref name="Rows"/> is enumerated, not derived from what was
/// actually read — so it's always safe for the caller to persist it as the new watermark once every
/// row in <paramref name="Rows"/> has been successfully staged and applied.
/// <para>
/// <paramref name="Diagnostics"/>, when supplied, is filled in *during* enumeration and is only
/// meaningful once <paramref name="Rows"/> has been fully consumed. Optional because most readers
/// have nothing to report.
/// </para>
/// <para>
/// <paramref name="Bounded"/> is the exception to the up-front rule above, and the reason callers
/// should persist <see cref="WatermarkAfterRead"/> rather than <paramref name="NewWatermark"/>. A read
/// capped by row count cannot know its next position before it runs — the position *is* the last row
/// it emitted — so it fills this in while streaming. See <see cref="BoundedRead"/>.
/// </para>
/// <para>
/// <paramref name="NewWatermarkTimeUtc"/> is what the source itself says about *when*
/// <paramref name="NewWatermark"/> committed, for the readers whose mechanism can state it — see
/// phase 87. Optional, and null for every reader that has no such mapping; a null is always "no
/// figure", never a claim about the position.
/// </para>
/// </summary>
public sealed record ReadResult(
    IAsyncEnumerable<ChangeRow> Rows,
    string NewWatermark,
    ReadDiagnostics? Diagnostics = null,
    BoundedReadPosition? Bounded = null,
    DateTimeOffset? NewWatermarkTimeUtc = null)
{
    /// <summary>
    /// The position to store once <see cref="Rows"/> has been fully consumed and written.
    /// <para>
    /// For an unbounded read this is just <see cref="NewWatermark"/>, computed before the first row.
    /// A bounded read overrides it with the position it actually reached — but only when its own
    /// semantics say it should, which is why the decision lives in the reader and this is a plain
    /// null-coalesce. A Change Tracking pass that consumed its whole window advances to the window's
    /// end; one that was cut short advances only as far as its last row.
    /// </para>
    /// </summary>
    public string WatermarkAfterRead => Bounded?.Reached ?? NewWatermark;

    /// <summary>
    /// When the source says <see cref="WatermarkAfterRead"/> committed, or null when no reader here
    /// can say — the value cached beside the watermark so that reporting lag later costs the source
    /// nothing. See phase 87.
    /// <para>
    /// **Keyed off <see cref="BoundedReadPosition.Reached"/>, not a null-coalesce like the property
    /// above.** A bounded read stores the position it reached rather than the window's end, and those
    /// two positions have different times. Coalescing would hand back the window end's time whenever
    /// mapping the reached one failed — a time belonging to a position this pass did not store, which
    /// on a mapping draining a backlog under a row cap is exactly the case that would read as
    /// caught-up while it is furthest behind. A time and a position that disagree about which pass
    /// they came from are worse than no time at all, so the pairing is structural.
    /// </para>
    /// </summary>
    public DateTimeOffset? WatermarkTimeAfterRead =>
        Bounded?.Reached is null ? NewWatermarkTimeUtc : Bounded.ReachedTimeUtc;
}

/// <summary>
/// Counters a reader accumulates while streaming, for the caller to log once the stream is drained.
/// <para>
/// Deliberately mutable and read-after-the-fact, which is an awkward shape next to the immutable
/// record above. It exists because the alternative is silence: a reader that quietly drops rows —
/// even correctly — leaves an operator with a mapping that behaves oddly and nothing to connect it
/// to. See architecture/planning/done/task-run-errors-during-high-volume-workload.md, where exactly
/// that invisibility is what made the underlying bug expensive to find.
/// </para>
/// </summary>
public sealed class ReadDiagnostics
{
    /// <summary>
    /// Rows a change feed reported as an insert or update, but whose source row had already been
    /// deleted by the time the reader looked for it. Such a row carries no usable values, so it is
    /// skipped; the deletion arrives as its own change on a later pass.
    /// </summary>
    public int RowsSkippedSourceRowGone { get; set; }
}
