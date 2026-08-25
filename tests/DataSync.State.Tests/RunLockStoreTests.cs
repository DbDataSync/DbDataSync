namespace DataSync.State.Tests;

public sealed class RunLockStoreTests : IDisposable
{
    private readonly string _tempDir = Directory.CreateTempSubdirectory("datasync-state-tests-").FullName;
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
        Assert.True(_store.TryAcquire("crm-sync", Guid.NewGuid()));
        Assert.True(_store.IsLocked("crm-sync"));
    }

    [Fact]
    public void TryAcquire_WhenAlreadyLocked_Fails()
    {
        Assert.True(_store.TryAcquire("crm-sync", Guid.NewGuid()));
        Assert.False(_store.TryAcquire("crm-sync", Guid.NewGuid()));
    }

    [Fact]
    public void Release_ThenTryAcquire_Succeeds()
    {
        _store.TryAcquire("crm-sync", Guid.NewGuid());
        _store.Release("crm-sync");

        Assert.False(_store.IsLocked("crm-sync"));
        Assert.True(_store.TryAcquire("crm-sync", Guid.NewGuid()));
    }

    [Fact]
    public async Task TryAcquire_UnderConcurrentContention_OnlyOneCallerWins()
    {
        var results = await Task.WhenAll(Enumerable.Range(0, 20)
            .Select(_ => Task.Run(() => _store.TryAcquire("crm-sync", Guid.NewGuid()))));

        Assert.Equal(1, results.Count(r => r));
    }
}
