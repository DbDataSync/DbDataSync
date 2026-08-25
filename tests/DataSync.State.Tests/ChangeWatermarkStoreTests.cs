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
        Assert.Null(_store.GetWatermark("crm-sync", "orders-db/App/dbo.Orders"));
    }

    [Fact]
    public void SetWatermark_ThenGet_RoundTrips()
    {
        _store.SetWatermark("crm-sync", "orders-db/App/dbo.Orders", "12345");
        Assert.Equal("12345", _store.GetWatermark("crm-sync", "orders-db/App/dbo.Orders"));
    }

    [Fact]
    public void SetWatermark_Twice_OverwritesPreviousValue()
    {
        _store.SetWatermark("crm-sync", "orders-db/App/dbo.Orders", "12345");
        _store.SetWatermark("crm-sync", "orders-db/App/dbo.Orders", "67890");

        Assert.Equal("67890", _store.GetWatermark("crm-sync", "orders-db/App/dbo.Orders"));
    }

    [Fact]
    public void SetWatermark_IsScopedPerTaskAndTable()
    {
        _store.SetWatermark("crm-sync", "orders-db/App/dbo.Orders", "111");
        _store.SetWatermark("crm-sync", "orders-db/App/dbo.Customers", "222");
        _store.SetWatermark("other-sync", "orders-db/App/dbo.Orders", "333");

        Assert.Equal("111", _store.GetWatermark("crm-sync", "orders-db/App/dbo.Orders"));
        Assert.Equal("222", _store.GetWatermark("crm-sync", "orders-db/App/dbo.Customers"));
        Assert.Equal("333", _store.GetWatermark("other-sync", "orders-db/App/dbo.Orders"));
    }
}
