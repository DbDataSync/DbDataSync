using DbDataSync.Core.Config;

namespace DbDataSync.State.Tests;

public sealed class ChangeWatermarkStoreTests : IDisposable
{
    private readonly string _tempDir = Directory.CreateTempSubdirectory("dbdatasync-state-tests-").FullName;
    private readonly ChangeWatermarkStore _store;

    public ChangeWatermarkStoreTests()
    {
        var database = new StateDatabase(Path.Combine(_tempDir, "state.db"));
        _store = new ChangeWatermarkStore(database);
    }

    public void Dispose() => Directory.Delete(_tempDir, recursive: true);

    [Fact]
    public void GetWatermark_WhenNoneSet_ReturnsNull()
    {
        Assert.Null(_store.GetWatermark("crm-sync", "orders", "orders-db/App/dbo.Orders"));
    }

    [Fact]
    public void SetWatermark_ThenGet_RoundTrips()
    {
        _store.SetWatermark("crm-sync", "orders", "orders-db/App/dbo.Orders", "12345");
        Assert.Equal("12345", _store.GetWatermark("crm-sync", "orders", "orders-db/App/dbo.Orders"));
    }

    [Fact]
    public void SetWatermark_Twice_OverwritesPreviousValue()
    {
        _store.SetWatermark("crm-sync", "orders", "orders-db/App/dbo.Orders", "12345");
        _store.SetWatermark("crm-sync", "orders", "orders-db/App/dbo.Orders", "67890");

        Assert.Equal("67890", _store.GetWatermark("crm-sync", "orders", "orders-db/App/dbo.Orders"));
    }

    [Fact]
    public void SetWatermark_IsScopedPerTaskMappingAndTable()
    {
        _store.SetWatermark("crm-sync", "orders", "orders-db/App/dbo.Orders", "111");
        _store.SetWatermark("crm-sync", "orders", "orders-db/App/dbo.Customers", "222");
        _store.SetWatermark("other-sync", "orders", "orders-db/App/dbo.Orders", "333");
        _store.SetWatermark("crm-sync", "orders-audit", "orders-db/App/dbo.Orders", "444");

        Assert.Equal("111", _store.GetWatermark("crm-sync", "orders", "orders-db/App/dbo.Orders"));
        Assert.Equal("222", _store.GetWatermark("crm-sync", "orders", "orders-db/App/dbo.Customers"));
        Assert.Equal("333", _store.GetWatermark("other-sync", "orders", "orders-db/App/dbo.Orders"));
        Assert.Equal("444", _store.GetWatermark("crm-sync", "orders-audit", "orders-db/App/dbo.Orders"));
    }

    /// <summary>
    /// The bug phase 74 exists for, at the store's own level: two mappings in one replication reading
    /// the same physical table. Under the old <c>(TaskName, SourceTable)</c> key these were one row,
    /// and the second write silently became the first mapping's position too.
    /// </summary>
    [Fact]
    public void TwoMappingsOnTheSameSourceTable_KeepSeparatePositions()
    {
        const string table = "orders-db/App/dbo.Orders";

        _store.SetWatermark("crm-sync", "orders-by-version", table, "4210");
        _store.SetWatermark("crm-sync", "orders-by-modified-at", table, "2026-08-31T09:00:00Z");

        Assert.Equal("4210", _store.GetWatermark("crm-sync", "orders-by-version", table));
        Assert.Equal("2026-08-31T09:00:00Z", _store.GetWatermark("crm-sync", "orders-by-modified-at", table));
    }

    /// <summary>
    /// Clearing is scoped the same way. A resync of one mapping must not put another mapping on the
    /// same table back to the beginning — which, sharing a row, is exactly what it used to do.
    /// </summary>
    [Fact]
    public void ClearWatermark_LeavesTheOtherMappingOnTheSameTableAlone()
    {
        const string table = "orders-db/App/dbo.Orders";
        _store.SetWatermark("crm-sync", "orders-by-version", table, "4210");
        _store.SetWatermark("crm-sync", "orders-by-modified-at", table, "2026-08-31T09:00:00Z");

        Assert.True(_store.ClearWatermark("crm-sync", "orders-by-version", table));

        Assert.Null(_store.GetWatermark("crm-sync", "orders-by-version", table));
        Assert.Equal("2026-08-31T09:00:00Z", _store.GetWatermark("crm-sync", "orders-by-modified-at", table));
    }

    // ---- The cached commit time beside the position (phase 87) ----------------------------------

    /// <summary>
    /// The pairing the column exists for: the time a lag report reads back is the one the pass that
    /// stored *this* position supplied, written in the same statement so the two can never come from
    /// two different passes.
    /// </summary>
    [Fact]
    public void SetWatermark_StoresTheSourcesTimeForThatPosition_AndReadsItBackWithIt()
    {
        var committed = new DateTimeOffset(2026, 3, 1, 11, 55, 0, TimeSpan.Zero);
        _store.SetWatermark("crm-sync", "orders", "orders-db/App/dbo.Orders", "12345", committed);

        var applied = _store.GetAppliedPosition("crm-sync", "orders", "orders-db/App/dbo.Orders");

        Assert.NotNull(applied);
        Assert.Equal("12345", applied.Watermark);
        Assert.Equal(committed, applied.WatermarkTimeUtc);
    }

    /// <summary>
    /// A reader that has no position-to-time mapping, or a pass where the engine declined to place
    /// the position. The position is still the outcome and is still stored; the missing time is a lag
    /// figure nobody gets this pass, which is what a null says.
    /// </summary>
    [Fact]
    public void SetWatermark_WithNoTime_StoresThePositionAndANullTime()
    {
        _store.SetWatermark("crm-sync", "orders", "orders-db/App/dbo.Orders", "12345");

        var applied = _store.GetAppliedPosition("crm-sync", "orders", "orders-db/App/dbo.Orders");

        Assert.NotNull(applied);
        Assert.Equal("12345", applied.Watermark);
        Assert.Null(applied.WatermarkTimeUtc);
    }

    /// <summary>
    /// **A later pass that cannot state a time clears the earlier one rather than leaving it.** A
    /// stale time beside a moved position is the one outcome worse than no time at all: it reads as a
    /// current figure and is silently about a position the mapping has already passed.
    /// </summary>
    [Fact]
    public void SetWatermark_WithoutATime_ClearsATimeAnEarlierPassStored()
    {
        var committed = new DateTimeOffset(2026, 3, 1, 11, 55, 0, TimeSpan.Zero);
        _store.SetWatermark("crm-sync", "orders", "orders-db/App/dbo.Orders", "12345", committed);
        _store.SetWatermark("crm-sync", "orders", "orders-db/App/dbo.Orders", "67890");

        var applied = _store.GetAppliedPosition("crm-sync", "orders", "orders-db/App/dbo.Orders");

        Assert.Equal("67890", applied!.Watermark);
        Assert.Null(applied.WatermarkTimeUtc);
    }

    [Fact]
    public void GetAppliedPosition_WhenTheMappingHasNeverRun_ReturnsNull() =>
        Assert.Null(_store.GetAppliedPosition("crm-sync", "orders", "orders-db/App/dbo.Orders"));

    // ---- Read intent and hold, and Watermark going nullable (phase 100) --------------------------

    [Fact]
    public void GetReadState_WhenNoneStored_ReturnsNull() =>
        Assert.Null(_store.GetReadState("crm-sync", "orders", "orders-db/App/dbo.Orders"));

    /// <summary>
    /// The migration's own backfill claim, exercised through the mechanism that actually produces it:
    /// <c>ALTER TABLE ... ADD COLUMN ReadIntent {{text}} NOT NULL DEFAULT 'Changes'</c> gives every row
    /// that already existed — written by code with no notion of an intent at all — exactly this value,
    /// the moment the column is added. <see cref="ChangeWatermarkStore.SetWatermark"/> is that code: it
    /// never mentions <c>ReadIntent</c>/<c>ReadHold</c>, so a row it writes is indistinguishable, at the
    /// database level, from a row the migration found already there.
    /// </summary>
    [Fact]
    public void ARowWrittenWithNoNotionOfIntent_ReadsBackAsChangesAndNone()
    {
        _store.SetWatermark("crm-sync", "orders", "orders-db/App/dbo.Orders", "12345");

        var state = _store.GetReadState("crm-sync", "orders", "orders-db/App/dbo.Orders");

        Assert.NotNull(state);
        Assert.Equal(ReadIntent.Changes, state!.Intent);
        Assert.Equal(ReadHold.None, state.Hold);
        Assert.Equal("12345", state.Watermark);
    }

    /// <summary>
    /// The row shape the old schema could not express at all: an intent with no position behind it yet
    /// — <c>ChangesFromEarliest</c> on a mapping that has not completed its first pass under it.
    /// </summary>
    [Fact]
    public void SetReadIntent_WithNoWatermarkEverSet_StoresTheIntentAndANullWatermark()
    {
        _store.SetReadIntent("crm-sync", "orders", "orders-db/App/dbo.Orders", ReadIntent.ChangesFromEarliest);

        var state = _store.GetReadState("crm-sync", "orders", "orders-db/App/dbo.Orders");

        Assert.NotNull(state);
        Assert.Equal(ReadIntent.ChangesFromEarliest, state!.Intent);
        Assert.Equal(ReadHold.None, state.Hold);
        Assert.Null(state.Watermark);
    }

    [Fact]
    public void SetReadIntent_ThenGet_RoundTrips()
    {
        _store.SetReadIntent("crm-sync", "orders", "orders-db/App/dbo.Orders", ReadIntent.ChangesFromLatest);
        Assert.Equal(
            ReadIntent.ChangesFromLatest,
            _store.GetReadState("crm-sync", "orders", "orders-db/App/dbo.Orders")!.Intent);
    }

    [Fact]
    public void SetReadHold_ThenGet_RoundTrips()
    {
        _store.SetReadHold("crm-sync", "orders", "orders-db/App/dbo.Orders", ReadHold.Paused);
        Assert.Equal(
            ReadHold.Paused,
            _store.GetReadState("crm-sync", "orders", "orders-db/App/dbo.Orders")!.Hold);
    }

    /// <summary>Setting the intent must not touch a hold or a position already sitting on the row —
    /// the property that makes "set each independently" true rather than aspirational.</summary>
    [Fact]
    public void SetReadIntent_LeavesAnExistingHoldAndWatermarkAlone()
    {
        const string table = "orders-db/App/dbo.Orders";
        _store.SetWatermark("crm-sync", "orders", table, "999");
        _store.SetReadHold("crm-sync", "orders", table, ReadHold.PositionExpired);

        _store.SetReadIntent("crm-sync", "orders", table, ReadIntent.ChangesFromEarliest);

        var state = _store.GetReadState("crm-sync", "orders", table)!;
        Assert.Equal(ReadIntent.ChangesFromEarliest, state.Intent);
        Assert.Equal(ReadHold.PositionExpired, state.Hold);
        Assert.Equal("999", state.Watermark);
    }

    /// <summary>The other direction: a hold set on a mapping mid-<c>ChangesFromEarliest</c> must not
    /// silently rewrite the intent underneath it — the whole reason a hold is a column of its own.</summary>
    [Fact]
    public void SetReadHold_LeavesAnExistingIntentAndWatermarkAlone()
    {
        const string table = "orders-db/App/dbo.Orders";
        _store.SetWatermark("crm-sync", "orders", table, "999");
        _store.SetReadIntent("crm-sync", "orders", table, ReadIntent.ChangesFromEarliest);

        _store.SetReadHold("crm-sync", "orders", table, ReadHold.PositionExpired);

        var state = _store.GetReadState("crm-sync", "orders", table)!;
        Assert.Equal(ReadIntent.ChangesFromEarliest, state.Intent);
        Assert.Equal(ReadHold.PositionExpired, state.Hold);
        Assert.Equal("999", state.Watermark);
    }

    /// <summary>A pass advancing the watermark must not reset the intent back to a stale value or clear
    /// a hold nobody resolved — <c>SetWatermark</c> only ever touches its own two columns.</summary>
    [Fact]
    public void SetWatermark_LeavesAnExistingIntentAndHoldAlone()
    {
        const string table = "orders-db/App/dbo.Orders";
        _store.SetReadIntent("crm-sync", "orders", table, ReadIntent.ChangesFromEarliest);
        _store.SetReadHold("crm-sync", "orders", table, ReadHold.PositionExpired);

        _store.SetWatermark("crm-sync", "orders", table, "42");

        var state = _store.GetReadState("crm-sync", "orders", table)!;
        Assert.Equal(ReadIntent.ChangesFromEarliest, state.Intent);
        Assert.Equal(ReadHold.PositionExpired, state.Hold);
        Assert.Equal("42", state.Watermark);
    }

    /// <summary>The one call that moves both at once — an operator recovering from a hold, in a single
    /// write rather than two that could be observed half-done.</summary>
    [Fact]
    public void SetReadIntentAndHold_SetsBothInOneWrite()
    {
        const string table = "orders-db/App/dbo.Orders";
        _store.SetReadHold("crm-sync", "orders", table, ReadHold.PositionExpired);

        _store.SetReadIntentAndHold("crm-sync", "orders", table, ReadIntent.ChangesFromEarliest, ReadHold.None);

        var state = _store.GetReadState("crm-sync", "orders", table)!;
        Assert.Equal(ReadIntent.ChangesFromEarliest, state.Intent);
        Assert.Equal(ReadHold.None, state.Hold);
    }

    // ---- Phase 134: SetPendingLoad / PromotePendingLoad ----

    /// <summary>The captured position lands as Pending, never as the live Watermark — a crashed or
    /// still-running load must not read as a completed position.</summary>
    [Fact]
    public void SetPendingLoad_StashesThePositionAsPending_NeverAsTheLiveWatermark()
    {
        const string table = "orders-db/App/dbo.Orders";

        _store.SetPendingLoad("crm-sync", "orders", table, "12345", DateTimeOffset.UtcNow, "batch-1");

        var state = _store.GetReadState("crm-sync", "orders", table)!;
        Assert.Equal(ReadHold.Loading, state.Hold);
        Assert.Null(state.Watermark);
    }

    /// <summary>The one act: the pending position becomes the live one, the hold clears, the intent
    /// flips to Changes, and the pending columns are wiped — all in one write.</summary>
    [Fact]
    public void PromotePendingLoad_MovesThePendingPositionToLive_ClearsTheHold_FlipsTheIntent()
    {
        const string table = "orders-db/App/dbo.Orders";
        var capturedTime = DateTimeOffset.UtcNow;
        _store.SetPendingLoad("crm-sync", "orders", table, "999", capturedTime, "batch-1");

        var promoted = _store.PromotePendingLoad("batch-1");

        Assert.True(promoted);
        var state = _store.GetReadState("crm-sync", "orders", table)!;
        Assert.Equal(ReadIntent.Changes, state.Intent);
        Assert.Equal(ReadHold.None, state.Hold);
        Assert.Equal("999", state.Watermark);
        Assert.Equal(capturedTime, state.WatermarkTimeUtc);
    }

    /// <summary>A crashed or still-running load — pending fields written, batch never completed — must
    /// leave the live watermark untouched and the mapping still held. Nothing calls PromotePendingLoad
    /// in that case, so this is really just confirming SetPendingLoad alone changes nothing else.</summary>
    [Fact]
    public void ACrashedLoad_LeavesTheLiveWatermarkUntouched_AndTheMappingStillHeld()
    {
        const string table = "orders-db/App/dbo.Orders";
        _store.SetWatermark("crm-sync", "orders", table, "111");
        _store.SetReadIntent("crm-sync", "orders", table, ReadIntent.Changes);

        _store.SetPendingLoad("crm-sync", "orders", table, "999", null, "batch-1");

        var state = _store.GetReadState("crm-sync", "orders", table)!;
        Assert.Equal(ReadHold.Loading, state.Hold);
        // The live watermark is untouched by the pending write — only PromotePendingLoad ever moves it.
        Assert.Equal("111", state.Watermark);
    }

    /// <summary>An ordinary operator-triggered reload shares RunKind.BulkLoad and the same table without
    /// gating anything — completing one of those must promote nothing. PendingBulkLoadBatchId is what
    /// tells the two apart.</summary>
    [Fact]
    public void PromotePendingLoad_WhenNoRowIsWaitingOnThisBatch_PromotesNothing()
    {
        const string table = "orders-db/App/dbo.Orders";
        _store.SetPendingLoad("crm-sync", "orders", table, "999", null, "batch-1");

        var promoted = _store.PromotePendingLoad("some-unrelated-batch");

        Assert.False(promoted);
        var state = _store.GetReadState("crm-sync", "orders", table)!;
        Assert.Equal(ReadHold.Loading, state.Hold);
        Assert.Null(state.Watermark);
    }
}
