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
}
