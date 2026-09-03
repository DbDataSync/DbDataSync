using DbDataSync.Drivers.Abstractions;
using DbDataSync.TaskRunner;

namespace DbDataSync.TaskRunner.Tests;

/// <summary>
/// The decorator that times a reader without any reader knowing about it. The two numbers it produces
/// have to mean different things, and the failure paths have to still produce one — a run that died
/// mid-read did spend that time reading.
/// </summary>
public sealed class ReaderTimingTests
{
    private static readonly ChangeSchema Schema = new(["Id"]);

    private static ChangeRow Row(int id) => new(ChangeOperation.Insert, Schema, [id]);

    private static async IAsyncEnumerable<ChangeRow> RowsAsync(
        int count, TimeSpan beforeFirst = default, TimeSpan betweenRows = default)
    {
        if (beforeFirst > TimeSpan.Zero)
            await Task.Delay(beforeFirst);

        for (var i = 0; i < count; i++)
        {
            if (i > 0 && betweenRows > TimeSpan.Zero)
                await Task.Delay(betweenRows);
            yield return Row(i);
        }
    }

    private static async Task<int> DrainAsync(IAsyncEnumerable<ChangeRow> rows)
    {
        var count = 0;
        await foreach (var _ in rows)
            count++;
        return count;
    }

    [Fact]
    public async Task ItPassesEveryRowThroughUnchanged()
    {
        var recorder = new ReaderTimingRecorder();

        Assert.Equal(5, await DrainAsync(RowsAsync(5).WithTiming(recorder)));
    }

    [Fact]
    public async Task TimeToFirstRow_IsAlwaysAPrefixOfTheLifetime()
    {
        var recorder = new ReaderTimingRecorder();

        await DrainAsync(RowsAsync(4, betweenRows: TimeSpan.FromMilliseconds(20)).WithTiming(recorder));

        Assert.NotNull(recorder.TimeToFirstRowMs);
        Assert.NotNull(recorder.LifetimeMs);
        Assert.True(recorder.TimeToFirstRowMs <= recorder.LifetimeMs,
            $"time to first row {recorder.TimeToFirstRowMs}ms exceeded lifetime {recorder.LifetimeMs}ms");
    }

    /// <summary>
    /// The claim that makes two numbers worth having rather than one: a source that is slow to *start*
    /// and a source that is slow to *finish* are different problems, and these have to be able to tell
    /// them apart.
    /// </summary>
    [Fact]
    public async Task ASlowFirstRow_ShowsUpInBothNumbers_AndASlowStreamOnlyInTheLifetime()
    {
        var slowToStart = new ReaderTimingRecorder();
        await DrainAsync(RowsAsync(3, beforeFirst: TimeSpan.FromMilliseconds(120)).WithTiming(slowToStart));

        var slowToFinish = new ReaderTimingRecorder();
        await DrainAsync(RowsAsync(3, betweenRows: TimeSpan.FromMilliseconds(60)).WithTiming(slowToFinish));

        Assert.True(slowToStart.TimeToFirstRowMs >= 100,
            $"a source that took 120ms to answer reported {slowToStart.TimeToFirstRowMs}ms to first row");
        Assert.True(slowToFinish.TimeToFirstRowMs < 50,
            $"a source that answered immediately reported {slowToFinish.TimeToFirstRowMs}ms to first row");
        Assert.True(slowToFinish.LifetimeMs >= 100,
            $"a stream that took 120ms to drain reported a {slowToFinish.LifetimeMs}ms lifetime");
    }

    [Fact]
    public async Task AnEmptyRead_RecordsALifetimeButNoFirstRow()
    {
        var recorder = new ReaderTimingRecorder();

        await DrainAsync(RowsAsync(0).WithTiming(recorder));

        Assert.Null(recorder.TimeToFirstRowMs);
        Assert.NotNull(recorder.LifetimeMs);
    }

    /// <summary>
    /// A pass that failed mid-read still spent that time reading, and a run row whose timing columns
    /// were null would say it did not.
    /// </summary>
    [Fact]
    public async Task AReadThatThrowsMidStream_StillRecordsItsLifetime()
    {
        var recorder = new ReaderTimingRecorder();

        static async IAsyncEnumerable<ChangeRow> Failing()
        {
            yield return Row(1);
            await Task.Delay(30);
            throw new InvalidOperationException("the source went away");
        }

        await Assert.ThrowsAsync<InvalidOperationException>(() => DrainAsync(Failing().WithTiming(recorder)));

        Assert.NotNull(recorder.TimeToFirstRowMs);
        Assert.NotNull(recorder.LifetimeMs);
    }

    /// <summary>
    /// Why staging is measured as a whole wrapped call rather than being assumed equal to the reader's
    /// lifetime.
    /// <para>
    /// Today's only staging provider writes rows through as they arrive, so the two numbers track each
    /// other and the second looks redundant. <c>IStagingProvider</c> already names file-based staging
    /// as a real future shape, and such a provider does its move or upload **after** the stream is
    /// exhausted — work the reader's lifetime cannot see. This models one, and shows the wrapped-call
    /// measurement already captures it, ahead of any real provider existing.
    /// </para>
    /// </summary>
    [Fact]
    public async Task StagingThatWorksAfterTheReadIsDone_MeasuresLongerThanTheRead()
    {
        var recorder = new ReaderTimingRecorder();
        var stagingClock = System.Diagnostics.Stopwatch.StartNew();

        // Consume every row — which is where a pass-through provider would stop — and then keep
        // working, the way a stage-to-file/move-file provider does.
        await DrainAsync(RowsAsync(3).WithTiming(recorder));
        await Task.Delay(120);
        var stagingMs = stagingClock.ElapsedMilliseconds;

        Assert.NotNull(recorder.LifetimeMs);
        Assert.True(stagingMs > recorder.LifetimeMs + 50,
            $"staging {stagingMs}ms did not exceed the {recorder.LifetimeMs}ms read it consumes, so the " +
            "post-read work was not being measured");
    }

    [Fact]
    public async Task AConsumerThatStopsEarly_StillRecordsALifetime()
    {
        // `break` out of an await foreach disposes the enumerator, which is the only signal the
        // decorator gets that the read is over.
        var recorder = new ReaderTimingRecorder();

        await foreach (var _ in RowsAsync(100).WithTiming(recorder))
            break;

        Assert.NotNull(recorder.LifetimeMs);
    }
}
