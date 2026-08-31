using DataSync.Core.Config;

namespace DataSync.Drivers.Abstractions;

/// <summary>
/// How a writer splits one staged change set into several smaller statements, so a large apply does
/// not hold the target's locks for the whole of it.
/// <para>
/// The unit chunked over is the staging table's ordinal column — a per-pass identity a staging
/// provider fills in on the way in, never anything the source supplied. Which column that is stays a
/// driver-internal detail: this type only decides *how many* rows a chunk carries, because that is the
/// part every writer answers the same way.
/// </para>
/// <para>
/// **This does not make an apply resumable.** The watermark still advances only after the whole pass
/// succeeds, so a run killed between chunks redoes every chunk on its next attempt. Per-chunk durable
/// progress is a change to the watermark model and is deliberately a later phase — see
/// <c>architecture/planning/done/chunked-apply-and-bounded-reads.md</c>, option B.
/// </para>
/// </summary>
public static class ApplyBatch
{
    /// <summary>Option key, so the writers, the descriptor and the tests agree on one spelling.</summary>
    public const string OptionName = "applyBatchSize";

    /// <summary>
    /// Rows per chunk when the option is not set.
    /// <para>
    /// 5,000 because that is SQL Server's lock-escalation threshold: a statement that takes more than
    /// roughly that many row/page locks on one table is escalated to a table lock, which is precisely
    /// the "everything else waits behind the writer" symptom this exists to relieve. Staying just under
    /// it is the difference between a chunk that blocks one range and a chunk that blocks the table.
    /// The other engines in scope have no equivalent cliff, so a number chosen for the one that does
    /// is not a bad number for them — it is merely arbitrary, where here it is not.
    /// </para>
    /// </summary>
    public const int DefaultSize = 5_000;

    /// <summary>Declared once and reused, so a writer offering this setting describes it identically.</summary>
    public static ParameterDescriptor Descriptor { get; } = new()
    {
        Name = OptionName,
        Label = "Apply batch size",
        Description =
            "How many staged rows one write statement carries. Smaller batches hold the target's locks " +
            $"for shorter stretches at the cost of more statements. Defaults to {DefaultSize}; set 0 to " +
            "apply the whole staged set in one statement, as this writer did before batching existed.",
        Type = ParameterType.Number,
        Default = DefaultSize.ToString(),
    };

    /// <summary>
    /// The configured chunk size, or null for "one statement over everything".
    /// <para>
    /// An unparseable or negative value falls back to the default rather than throwing: the number is a
    /// performance knob, and failing a run over a typo in one would be a worse outcome than applying it
    /// at a sensible size. Zero is the one value that means something — it is how an operator asks for
    /// the unchunked statement back.
    /// </para>
    /// </summary>
    public static int? Read(IReadOnlyDictionary<string, string> options)
    {
        if (!options.TryGetValue(OptionName, out var raw) || string.IsNullOrWhiteSpace(raw))
            return DefaultSize;

        if (!int.TryParse(raw.Trim(), out var size) || size < 0)
            return DefaultSize;

        // Zero, and only zero, is the escape hatch. A negative number is a typo, not a request.
        return size == 0 ? null : size;
    }

    /// <summary>
    /// The half-open ordinal ranges to apply, in order: <c>(exclusive lower bound, inclusive upper
    /// bound]</c>, which is what lets the first chunk start at 0 against an identity that starts at 1.
    /// <para>
    /// Derived from the staged row count rather than from a <c>MIN</c>/<c>MAX</c> round trip, because a
    /// staging table is created fresh per pass and only ever inserted into — so its ordinals are
    /// 1..<paramref name="rowCount"/> with nothing else in the table to range over. Should that ever
    /// stop being true, ranging still covers every row; the chunks merely stop being evenly sized.
    /// </para>
    /// </summary>
    public static IEnumerable<(long After, long UpTo)> Ranges(long rowCount, int? batchSize)
    {
        if (batchSize is not { } size || rowCount <= size)
        {
            if (rowCount > 0)
                yield return (0, rowCount);
            yield break;
        }

        for (long after = 0; after < rowCount; after += size)
            yield return (after, Math.Min(after + size, rowCount));
    }
}
