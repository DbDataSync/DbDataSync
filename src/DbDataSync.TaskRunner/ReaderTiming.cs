using System.Diagnostics;
using System.Runtime.CompilerServices;
using DbDataSync.Drivers.Abstractions;

namespace DbDataSync.TaskRunner;

/// <summary>
/// Collects one pass's reader timings while its rows stream — see phase 59.
/// <para>
/// Mutable and read-after-the-fact, like <see cref="ReadDiagnostics"/> and for the same reason: the
/// numbers cannot exist before the read happens.
/// </para>
/// </summary>
public sealed class ReaderTimingRecorder
{
    private readonly Stopwatch _clock = Stopwatch.StartNew();

    /// <summary>How long the source took to produce its first row, or null if it produced none.</summary>
    public long? TimeToFirstRowMs { get; private set; }

    /// <summary>How long the row stream was alive, measured to its disposal.</summary>
    public long? LifetimeMs { get; private set; }

    internal void FirstRow() => TimeToFirstRowMs ??= _clock.ElapsedMilliseconds;

    internal void Finished() => LifetimeMs = _clock.ElapsedMilliseconds;
}

public static class ReaderTiming
{
    /// <summary>
    /// The same rows, timed on the way past.
    /// <para>
    /// **A decorator over the stream, not instrumentation inside the readers.** There are six readers
    /// and there will be more; adding a stopwatch to each would be six places to keep in step, six
    /// chances to measure subtly different things, and a change to <see cref="IChangeReader"/> that
    /// every future driver would have to know about. Wrapping the one thing they all return costs
    /// nothing and cannot drift.
    /// </para>
    /// <para>
    /// Applied only when the mapping asked for it, so a run that did not opt in is handed the original
    /// enumerable and pays not even a delegate call.
    /// </para>
    /// </summary>
    public static async IAsyncEnumerable<ChangeRow> WithTiming(
        this IAsyncEnumerable<ChangeRow> rows,
        ReaderTimingRecorder recorder,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // try/finally rather than try/catch, which a `yield return` forbids — and the finally is the
        // point: a pass that fails or is cancelled mid-read still records the lifetime it had, rather
        // than reporting nothing for a run that really did spend that time reading.
        var enumerator = rows.GetAsyncEnumerator(cancellationToken);
        try
        {
            while (await enumerator.MoveNextAsync())
            {
                recorder.FirstRow();
                yield return enumerator.Current;
            }
        }
        finally
        {
            recorder.Finished();
            await enumerator.DisposeAsync();
        }
    }
}
