using DbDataSync.Core.Config;
using DbDataSync.State;
using DbDataSync.TaskRunner;
using Xunit;

namespace DbDataSync.TaskRunner.Tests;

/// <summary>
/// Pins the rule <see cref="PrimaryPassOutcome"/> exists to state directly against the state store,
/// without a source or target connection: every intent transitions to <see cref="ReadIntent.Changes"/>
/// once a <c>Primary</c> pass applies its changes, and a pass that read nothing new changes neither the
/// watermark nor the intent.
/// </summary>
public sealed class PrimaryPassOutcomeTests : IDisposable
{
    private readonly string _dbPath;
    private readonly StateDatabase _database;
    private readonly ChangeWatermarkStore _watermarks;
    private readonly IRunnerState _state;

    public PrimaryPassOutcomeTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"dbdatasync-outcome-tests-{Guid.NewGuid():N}.db");
        _database = new StateDatabase(_dbPath);
        _watermarks = new ChangeWatermarkStore(_database);
        _state = new LocalRunnerState(
            new TaskRunStore(_database), new WorkQueueStore(_database), new RunLockStore(_database),
            _watermarks, new VerificationResultStore(_database), new LogWriter(_database),
            new BulkLoadBatchStore(_database), new Lazy<IInitialLoadEnqueuer>(() => new NeverCalledInitialLoadEnqueuer()));
    }

    public void Dispose()
    {
        if (File.Exists(_dbPath))
            File.Delete(_dbPath);
    }

    [Theory]
    [InlineData(ReadIntent.InitialLoad)]
    [InlineData(ReadIntent.ChangesFromEarliest)]
    [InlineData(ReadIntent.ChangesFromLatest)]
    [InlineData(ReadIntent.Changes)]
    public void Apply_ASuccessfulPass_TransitionsWhateverIntentItStartedFrom_ToChanges(ReadIntent startingIntent)
    {
        _watermarks.SetReadIntent("task", "mapping", "key", startingIntent);

        var change = PrimaryPassOutcome.Apply(
            _state, "task", "mapping", "key", previousWatermark: null, newWatermark: "42", newWatermarkTimeUtc: null);

        Assert.NotNull(change);
        Assert.Null(change!.Previous);
        Assert.Equal("42", change.New);
        Assert.Equal("42", _watermarks.GetWatermark("task", "mapping", "key"));
        Assert.Equal(ReadIntent.Changes, _watermarks.GetReadState("task", "mapping", "key")!.Intent);
    }

    [Fact]
    public void Apply_ASuccessfulPass_RecordsTheWatermarkTime()
    {
        var time = DateTimeOffset.UtcNow;

        PrimaryPassOutcome.Apply(_state, "task", "mapping", "key", "10", "20", time);

        var applied = _watermarks.GetAppliedPosition("task", "mapping", "key");
        Assert.Equal("20", applied!.Watermark);
        Assert.Equal(time, applied.WatermarkTimeUtc);
    }

    /// <summary>
    /// A pass that read nothing new (no <c>NewWatermark</c>) changes neither the watermark nor the
    /// intent — the same reasoning a failed pass leaves both alone for: there is nothing new to record,
    /// and writing the intent unconditionally here would transition a mapping stuck on
    /// <c>ChangesFromEarliest</c> to <c>Changes</c> despite that pass having applied nothing.
    /// </summary>
    [Fact]
    public void Apply_APassThatReadNothingNew_ChangesNeitherTheWatermarkNorTheIntent()
    {
        _watermarks.SetReadIntent("task", "mapping", "key", ReadIntent.ChangesFromEarliest);

        var change = PrimaryPassOutcome.Apply(
            _state, "task", "mapping", "key", previousWatermark: "10", newWatermark: null, newWatermarkTimeUtc: null);

        Assert.Null(change);
        Assert.Null(_watermarks.GetWatermark("task", "mapping", "key"));
        Assert.Equal(ReadIntent.ChangesFromEarliest, _watermarks.GetReadState("task", "mapping", "key")!.Intent);
    }

    [Fact]
    public void Apply_AlreadyAtChanges_StaysAtChanges_AndStillAdvancesTheWatermark()
    {
        _watermarks.SetReadIntent("task", "mapping", "key", ReadIntent.Changes);

        var change = PrimaryPassOutcome.Apply(_state, "task", "mapping", "key", "10", "11", null);

        Assert.NotNull(change);
        Assert.Equal("11", _watermarks.GetWatermark("task", "mapping", "key"));
        Assert.Equal(ReadIntent.Changes, _watermarks.GetReadState("task", "mapping", "key")!.Intent);
    }
}
