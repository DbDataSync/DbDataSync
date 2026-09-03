namespace DbDataSync.State.Tests;

public sealed class RunLockStoreTests : IDisposable
{
    private readonly string _tempDir = Directory.CreateTempSubdirectory("dbdatasync-state-tests-").FullName;
    private readonly RunLockStore _store;

    public RunLockStoreTests()
    {
        var database = new StateDatabase(Path.Combine(_tempDir, "state.db"));
        _store = new RunLockStore(database);
    }

    public void Dispose() => Directory.Delete(_tempDir, recursive: true);

    [Fact]
    public void TryAcquire_WhenUnlocked_Succeeds()
    {
        Assert.True(_store.TryAcquire("crm-sync", RunKind.Primary, "orders", Guid.NewGuid()));
        Assert.True(_store.IsLocked("crm-sync", RunKind.Primary, "orders"));
    }

    [Fact]
    public void TryAcquire_WhenAlreadyLocked_Fails()
    {
        Assert.True(_store.TryAcquire("crm-sync", RunKind.Primary, "orders", Guid.NewGuid()));
        Assert.False(_store.TryAcquire("crm-sync", RunKind.Primary, "orders", Guid.NewGuid()));
    }

    [Fact]
    public void Release_ThenTryAcquire_Succeeds()
    {
        _store.TryAcquire("crm-sync", RunKind.Primary, "orders", Guid.NewGuid());
        _store.Release("crm-sync", RunKind.Primary, "orders");

        Assert.False(_store.IsLocked("crm-sync", RunKind.Primary, "orders"));
        Assert.True(_store.TryAcquire("crm-sync", RunKind.Primary, "orders", Guid.NewGuid()));
    }

    [Fact]
    public async Task TryAcquire_UnderConcurrentContention_OnlyOneCallerWins()
    {
        var results = await Task.WhenAll(Enumerable.Range(0, 20)
            .Select(_ => Task.Run(() => _store.TryAcquire("crm-sync", RunKind.Primary, "orders", Guid.NewGuid()))));

        Assert.Equal(1, results.Count(r => r));
    }

    [Fact]
    public void DifferentMappings_OfTheSameReplication_LockIndependently()
    {
        Assert.True(_store.TryAcquire("crm-sync", RunKind.Primary, "orders", Guid.NewGuid()));
        Assert.True(_store.TryAcquire("crm-sync", RunKind.Primary, "customers", Guid.NewGuid()));

        Assert.True(_store.IsLocked("crm-sync", RunKind.Primary, "orders"));
        Assert.True(_store.IsLocked("crm-sync", RunKind.Primary, "customers"));
    }

    [Fact]
    public void PrimaryAndBackfill_OfTheSameMapping_LockIndependently()
    {
        Assert.True(_store.TryAcquire("crm-sync", RunKind.Primary, "orders", Guid.NewGuid()));
        Assert.True(_store.TryAcquire("crm-sync", RunKind.Backfill, "orders", Guid.NewGuid()));

        Assert.True(_store.IsLocked("crm-sync", RunKind.Primary, "orders"));
        Assert.True(_store.IsLocked("crm-sync", RunKind.Backfill, "orders"));
    }
}
