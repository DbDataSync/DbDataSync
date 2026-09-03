namespace DbDataSync.State.Tests;

/// <summary>
/// The index that makes a result findable. The result itself is a parquet file the runner wrote — this
/// says where it is, and has to survive a run being replayed from a journal.
/// </summary>
public sealed class VerificationResultStoreTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("dbdatasync-verification-index-").FullName;
    private readonly VerificationResultStore _store;

    public VerificationResultStoreTests() =>
        _store = new VerificationResultStore(new StateDatabase(Path.Combine(_root, "state.db")));

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private static VerificationResultRecord Record(
        Guid runId, string check = "rows", string task = "sales", int differing = 0, string path = "/tmp/a.parquet") =>
        new(0, runId, task, "orders", check, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow, GroupsCompared: 3, differing, path);

    [Fact]
    public void ARecordedResult_IsFoundAgainByReplication()
    {
        var runId = Guid.NewGuid();
        _store.Record(Record(runId, differing: 2));

        var listed = Assert.Single(_store.List("sales"));

        Assert.Equal(runId, listed.RunId);
        Assert.Equal("rows", listed.CheckName);
        Assert.Equal(2, listed.DifferingGroups);
        Assert.Equal(3, listed.GroupsCompared);
        Assert.True(listed.Id > 0);
    }

    /// <summary>
    /// A run replayed from a state journal applies its outcomes again — every recovery operation has
    /// to be idempotent, and indexing the same file twice would be two rows pointing at one result.
    /// </summary>
    [Fact]
    public void RecordingTheSameRunAndCheckTwice_LeavesOneRowWithTheLaterValues()
    {
        var runId = Guid.NewGuid();
        _store.Record(Record(runId, differing: 2, path: "/tmp/first.parquet"));
        _store.Record(Record(runId, differing: 5, path: "/tmp/second.parquet"));

        var listed = Assert.Single(_store.List("sales"));

        Assert.Equal(5, listed.DifferingGroups);
        Assert.Equal("/tmp/second.parquet", listed.ResultPath);
    }

    [Fact]
    public void TwoChecksInOneRun_AreTwoResults()
    {
        var runId = Guid.NewGuid();
        _store.Record(Record(runId, check: "rows"));
        _store.Record(Record(runId, check: "sums"));

        Assert.Equal(2, _store.List("sales").Count);
    }

    [Fact]
    public void AnotherReplicationsResults_AreNotListed()
    {
        _store.Record(Record(Guid.NewGuid(), task: "other"));

        Assert.Empty(_store.List("sales"));
    }

    [Fact]
    public void AResult_IsFetchableById_AndAnUnknownIdIsNull()
    {
        _store.Record(Record(Guid.NewGuid()));
        var listed = Assert.Single(_store.List("sales"));

        Assert.Equal("rows", _store.Get(listed.Id)!.CheckName);
        Assert.Null(_store.Get(listed.Id + 1000));
    }
}
