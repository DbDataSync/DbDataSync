using System.Diagnostics;

namespace DbDataSync.Benchmarks;

public sealed record Measurement(
    string Variant,
    double ElapsedMs,
    double AllocatedMb,
    double PeakManagedHeapMb,
    double PeakWorkingSetMb,
    int Gen0,
    int Gen1,
    int Gen2,
    double GcPauseMs)
{
    public const string Header =
        "variant                       ms   alloc MB   peak heap    peak WS   gen0  gen1  gen2   pause ms";

    public override string ToString() =>
        $"{Variant,-26} {ElapsedMs,8:N0} {AllocatedMb,10:N1} {PeakManagedHeapMb,11:N1} {PeakWorkingSetMb,10:N1} "
        + $"{Gen0,6} {Gen1,5} {Gen2,5} {GcPauseMs,10:N0}";

    /// <summary>Tab-separated, for a child process to hand back to the orchestrator.</summary>
    public string ToWire() => string.Join('\t',
        Variant, ElapsedMs.ToString("F0"), AllocatedMb.ToString("F1"), PeakManagedHeapMb.ToString("F1"),
        PeakWorkingSetMb.ToString("F1"), Gen0, Gen1, Gen2, GcPauseMs.ToString("F0"));

    public static Measurement FromWire(string line)
    {
        var f = line.Split('\t');
        return new Measurement(f[0], double.Parse(f[1]), double.Parse(f[2]), double.Parse(f[3]),
            double.Parse(f[4]), int.Parse(f[5]), int.Parse(f[6]), int.Parse(f[7]), double.Parse(f[8]));
    }
}

/// <summary>
/// Wraps one run with the counters that actually answer "does this reduce GC and stabilise memory".
/// <para>
/// Cumulative allocated bytes alone cannot: it cannot distinguish boxing every row up front from
/// boxing one cell transiently at a sink boundary, where it dies in gen0 immediately. Collection
/// counts, pause time and peak live heap can. Peak managed heap is sampled rather than read at the
/// end, because the end is exactly when it isn't peak.
/// </para>
/// </summary>
public static class Measured
{
    public static async Task<Measurement> RunAsync(string variant, Func<Task> body)
    {
        long peakManagedHeap = 0;
        using var sampling = new CancellationTokenSource();
        var sampler = Task.Run(async () =>
        {
            while (!sampling.IsCancellationRequested)
            {
                peakManagedHeap = Math.Max(peakManagedHeap, GC.GetTotalMemory(forceFullCollection: false));
                try { await Task.Delay(2, sampling.Token); }
                catch (OperationCanceledException) { return; }
            }
        });

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        var allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
        var pauseBefore = GC.GetTotalPauseDuration();
        var (gen0, gen1, gen2) = (GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2));

        var stopwatch = Stopwatch.StartNew();
        await body();
        stopwatch.Stop();

        await sampling.CancelAsync();
        await sampler;

        const double Mb = 1024 * 1024;
        return new Measurement(
            variant,
            stopwatch.Elapsed.TotalMilliseconds,
            (GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore) / Mb,
            peakManagedHeap / Mb,
            Process.GetCurrentProcess().PeakWorkingSet64 / Mb,
            GC.CollectionCount(0) - gen0,
            GC.CollectionCount(1) - gen1,
            GC.CollectionCount(2) - gen2,
            (GC.GetTotalPauseDuration() - pauseBefore).TotalMilliseconds);
    }
}
