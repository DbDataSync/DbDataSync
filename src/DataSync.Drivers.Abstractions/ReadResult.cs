namespace DataSync.Drivers.Abstractions;

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
/// </summary>
public sealed record ReadResult(
    IAsyncEnumerable<ChangeRow> Rows,
    string NewWatermark,
    ReadDiagnostics? Diagnostics = null);

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
