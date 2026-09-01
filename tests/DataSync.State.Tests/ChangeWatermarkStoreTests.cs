namespace DataSync.State.Tests;

public sealed class ChangeWatermarkStoreTests : IDisposable
{
    private readonly string _tempDir = Directory.CreateTempSubdirectory("datasync-state-tests-").FullName;
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
}
