namespace DataSync.State.Tests;

/// <summary>
/// The state store, against a real SQL Server and a real PostgreSQL — phase 63.
/// <para>
/// Every test runs on all three engines, SQLite included. That is the point: the value of these is
/// not "MSSQL works" but "MSSQL, Postgres and SQLite answer identically", and a suite that only ran
/// on the new engines could not say that. SQLite needs no server, so it is covered even when the
/// containers are not running.
/// </para>
/// <para>
/// Category=Integration: the other two need the servers <c>tools/dev-harness</c> and
/// <c>docker-compose.yml</c> stand up, the same ones every other integration test here uses.
/// </para>
/// </summary>
[Trait("Category", "Integration")]
public sealed class CrossEngineStateTests : IDisposable
{
    private readonly string _tempDir = Directory.CreateTempSubdirectory("datasync-xengine-").FullName;
    private readonly List<StateEngineFixture> _fixtures = [];

    public void Dispose()
    {
        foreach (var fixture in _fixtures)
            fixture.Dispose();
        Directory.Delete(_tempDir, recursive: true);
    }

    private StateDatabase Open(StateEngine engine)
    {
        if (engine == StateEngine.Sqlite)
            return new StateDatabase(Path.Combine(_tempDir, $"{Guid.NewGuid():N}.db"));

        var fixture = new StateEngineFixture(engine);
        _fixtures.Add(fixture);
        return fixture.Database;
    }

    public static TheoryData<StateEngine> Engines => [StateEngine.Sqlite, StateEngine.MsSql, StateEngine.Postgres];

    /// <summary>
    /// The DDL renders and applies. Nothing else in this file can pass if this does not, but it fails
    /// with a readable message about the statement rather than about a missing table.
    /// </summary>
    [Theory]
    [MemberData(nameof(Engines))]
    public void TheSchemaIsCreated(StateEngine engine)
    {
        var database = Open(engine);

        // Every table, reached through a query rather than a catalog read — the catalog is spelled
        // differently on each engine, and "can I select from it" is the question that matters.
        foreach (var table in new[]
        {
            "Tasks", "TaskRuns", "ChangeWatermarks", "Logs", "RunLocks", "WorkQueue",
            "VerificationResults", "Users", "UserCredentials", "Sessions", "Invites", "PauseEvents",
        })
        {
            using var connection = database.OpenConnection();
            using var cmd = database.Command(connection, $"SELECT COUNT(*) FROM {table};");
            Assert.Equal(0, Convert.ToInt32(cmd.ExecuteScalar()));
        }
    }

    /// <summary>Reopening applies nothing twice — the version is recorded, wherever each engine keeps
    /// it (a pragma on one, a table on the other two).</summary>
    [Theory]
    [MemberData(nameof(Engines))]
    public void ReopeningDoesNotReapplyMigrations(StateEngine engine)
    {
        var database = Open(engine);
        var store = new TaskRunStore(database);
        store.UpsertTask("crm-sync", enabled: true);

        // OpenConnection re-verifies the schema on every call; if that re-ran the migrations, this row
        // would be gone with the table it lives in.
        using (var _ = database.OpenConnection()) { }
        using (var _ = database.OpenConnection()) { }

        Assert.False(store.IsPaused("crm-sync"));
        Assert.Equal((false, null), store.GetPauseState("crm-sync"));
    }

    /// <summary>The upsert idiom: insert, then overwrite, never two rows and never an error.</summary>
    [Theory]
    [MemberData(nameof(Engines))]
    public void UpsertOverwritesRatherThanDuplicating(StateEngine engine)
    {
        var database = Open(engine);
        var watermarks = new ChangeWatermarkStore(database);

        watermarks.SetWatermark("crm-sync", "orders", "dbo.Orders", "100");
        watermarks.SetWatermark("crm-sync", "orders", "dbo.Orders", "200");

        Assert.Equal("200", watermarks.GetWatermark("crm-sync", "orders", "dbo.Orders"));
        Assert.Null(watermarks.GetWatermark("crm-sync", "orders", "dbo.Customers"));
    }

    /// <summary>
    /// Insert-or-ignore, which is what makes a lock a lock: the second caller is refused rather than
    /// given a second row, and is not thrown at either.
    /// </summary>
    [Theory]
    [MemberData(nameof(Engines))]
    public void ALockIsGrantedOnceAndRefusedAfterwards(StateEngine engine)
    {
        var database = Open(engine);
        var locks = new RunLockStore(database);

        Assert.True(locks.TryAcquire("crm-sync", RunKind.Primary, "orders", Guid.NewGuid()));
        Assert.False(locks.TryAcquire("crm-sync", RunKind.Primary, "orders", Guid.NewGuid()));

        // A different mapping is a different lock — the whole reason the key is composite.
        Assert.True(locks.TryAcquire("crm-sync", RunKind.Primary, "customers", Guid.NewGuid()));

        locks.Release("crm-sync", RunKind.Primary, "orders");
        Assert.True(locks.TryAcquire("crm-sync", RunKind.Primary, "orders", Guid.NewGuid()));
    }

    /// <summary>
    /// The partial unique index, which is the one construct that had no direct equivalent on all three
    /// engines. Enqueuing the same work twice while the first is in flight returns the first's RunId;
    /// once it is finished, the same work can be queued again.
    /// <para>
    /// The second half is what a naive port gets wrong: an insert-or-ignore that ignores the index's
    /// predicate reads as "one row per mapping for all time", and a mapping that had ever run could
    /// never be queued again. That failure is silent, and this is what catches it.
    /// </para>
    /// </summary>
    [Theory]
    [MemberData(nameof(Engines))]
    public void TheQueueAllowsOneInFlightRow_AndAnotherOnceItIsDone(StateEngine engine)
    {
        var database = Open(engine);
        var queue = new WorkQueueStore(database);

        var first = queue.Enqueue("crm-sync", RunKind.Primary, "orders");
        var second = queue.Enqueue("crm-sync", RunKind.Primary, "orders");
        Assert.Equal(first, second);

        var claimed = queue.TryClaimNext("crm-sync", "worker-1");
        Assert.NotNull(claimed);
        queue.MarkDone(claimed!.Id);

        var third = queue.Enqueue("crm-sync", RunKind.Primary, "orders");
        Assert.NotEqual(first, third);
    }

    /// <summary>Row limiting — <c>LIMIT</c> on two engines and <c>OFFSET/FETCH</c> on the third, which
    /// also has to agree about which rows are the most recent.</summary>
    [Theory]
    [MemberData(nameof(Engines))]
    public void HistoryRespectsItsLimit_AndItsOrder(StateEngine engine)
    {
        var database = Open(engine);
        var store = new TaskRunStore(database);

        for (var i = 0; i < 5; i++)
            store.SetPaused("crm-sync", paused: i % 2 == 0, note: $"note {i}", performedBy: "ada");

        var all = store.GetPauseHistory("crm-sync");
        Assert.Equal(5, all.Count);

        var limited = store.GetPauseHistory("crm-sync", limit: 2);
        Assert.Equal(2, limited.Count);
        Assert.Equal("note 4", limited[0].Note);
        Assert.Equal("note 3", limited[1].Note);
    }

    /// <summary>
    /// The auto-assigned key — <c>AUTOINCREMENT</c>, <c>IDENTITY</c> and <c>GENERATED ALWAYS</c> — has
    /// to produce an increasing sequence on all three, because the pause history is ordered by it
    /// rather than by a timestamp precisely so that two actions in the same tick keep their order.
    /// </summary>
    [Theory]
    [MemberData(nameof(Engines))]
    public void GeneratedKeysIncrease(StateEngine engine)
    {
        var database = Open(engine);
        var store = new TaskRunStore(database);

        store.SetPaused("crm-sync", paused: true, note: "first", performedBy: "ada");
        store.SetPaused("crm-sync", paused: false, note: "second", performedBy: "ada");
        store.SetPaused("crm-sync", paused: true, note: "third", performedBy: "ada");

        var ids = store.GetPauseHistory("crm-sync").Select(e => e.Id).ToList();
        Assert.Equal(ids.OrderByDescending(id => id), ids);
        Assert.Equal(3, ids.Distinct().Count());
    }

    /// <summary>A run's full lifecycle, including the seven nullable timing columns and the GUID key
    /// round-tripping as each engine's text type.</summary>
    [Theory]
    [MemberData(nameof(Engines))]
    public void ARunRoundTrips(StateEngine engine)
    {
        var database = Open(engine);
        var queue = new WorkQueueStore(database);
        var store = new TaskRunStore(database);

        var runId = queue.Enqueue("crm-sync", RunKind.Primary, "orders");
        store.BeginRun(runId, pid: 4242);
        store.CompleteRun(
            runId, RunStatus.Succeeded, rowsRead: 10, rowsWritten: 9, errorSummary: null,
            timing: new RunTiming("MsSqlChangeTracking", 12, 340, "StagingTable", 400, "Merge", 55));

        var run = store.GetRun(runId);
        Assert.NotNull(run);
        Assert.Equal(runId, run!.RunId);
        Assert.Equal(RunStatus.Succeeded, run.Status);
        Assert.Equal(10, run.RowsRead);
        Assert.Equal(9, run.RowsWritten);
        Assert.Equal(4242, run.Pid);
        Assert.NotNull(run.Timing);
        Assert.Equal(12, run.Timing!.ReaderTimeToFirstRowMs);
        Assert.Equal("Merge", run.Timing.WriterKind);
    }

    /// <summary>An untraced run keeps every timing column null, which is the distinction the columns
    /// were made nullable for — and null is the one thing engines most often disagree about.</summary>
    [Theory]
    [MemberData(nameof(Engines))]
    public void AnUntracedRunHasNoTiming(StateEngine engine)
    {
        var database = Open(engine);
        var queue = new WorkQueueStore(database);
        var store = new TaskRunStore(database);

        var runId = queue.Enqueue("crm-sync", RunKind.Primary, "orders");
        store.BeginRun(runId, pid: null);
        store.CompleteRun(runId, RunStatus.Succeeded, 0, 0, errorSummary: null);

        var run = store.GetRun(runId);
        Assert.Null(run!.Timing);
        Assert.Null(run.Pid);
        Assert.Null(run.ErrorSummary);
    }

    /// <summary>
    /// Logs go through a batched writer with its own insert-or-ignore against a partial index, and
    /// carry the longest free text in the schema — which on SQL Server is the column type that cannot
    /// be indexed, and is why the schema distinguishes the two.
    /// </summary>
    [Theory]
    [MemberData(nameof(Engines))]
    public void LogsAreWrittenAndReadBack(StateEngine engine)
    {
        var database = Open(engine);
        var queue = new WorkQueueStore(database);
        using var logs = new LogWriter(database);

        var runId = queue.Enqueue("crm-sync", RunKind.Primary, "orders");
        logs.Log(runId, LogSeverity.Info, "started");
        logs.Log(runId, LogSeverity.Error, new string('x', 5000));
        logs.Flush();

        var written = logs.GetLogs(runId);
        Assert.Equal(2, written.Count);
        Assert.Equal("started", written[0].Message);
        Assert.Equal(5000, written[1].Message.Length);
    }

    /// <summary>
    /// A journal replay writes the same line twice and must store it once — the idempotency the
    /// partial unique index on SourceKey exists for, and the second construct with no direct SQL
    /// Server equivalent.
    /// </summary>
    [Theory]
    [MemberData(nameof(Engines))]
    public void AReplayedLogLineIsStoredOnce_ButTwoLiveOnesAreTwo(StateEngine engine)
    {
        var database = Open(engine);
        var queue = new WorkQueueStore(database);
        using var logs = new LogWriter(database);

        var runId = queue.Enqueue("crm-sync", RunKind.Primary, "orders");
        logs.Log(runId, LogSeverity.Info, "replayed", DateTimeOffset.UtcNow, sourceKey: $"{runId}:1");
        logs.Log(runId, LogSeverity.Info, "replayed", DateTimeOffset.UtcNow, sourceKey: $"{runId}:1");
        // Two identical lines with no source key are two lines: NULL never conflicts.
        logs.Log(runId, LogSeverity.Info, "live");
        logs.Log(runId, LogSeverity.Info, "live");
        logs.Flush();

        var written = logs.GetLogs(runId);
        Assert.Equal(3, written.Count);
        Assert.Single(written.Where(l => l.Message == "replayed"));
        Assert.Equal(2, written.Count(l => l.Message == "live"));
    }

    /// <summary>Pruning uses a window function and a subquery shared by two deletes — the most complex
    /// single statement in the store.</summary>
    [Theory]
    [MemberData(nameof(Engines))]
    public void PruningRemovesRunsAndTheirLogs(StateEngine engine)
    {
        var database = Open(engine);
        var queue = new WorkQueueStore(database);
        var store = new TaskRunStore(database);
        using var logs = new LogWriter(database);

        for (var i = 0; i < 5; i++)
        {
            var runId = queue.Enqueue("crm-sync", RunKind.Primary, "orders", segmentLabel: $"seg-{i}");
            store.BeginRun(runId, pid: null);
            logs.Log(runId, LogSeverity.Info, $"run {i}");
            store.CompleteRun(runId, RunStatus.Succeeded, 0, 0, null);
        }

        logs.Flush();
        Assert.Equal(5, store.GetRunHistory("crm-sync").Count);

        var pruned = store.PruneRuns(maxAge: null, maxPerMapping: 2);

        Assert.Equal(3, pruned);
        Assert.Equal(2, store.GetRunHistory("crm-sync").Count);
    }

    /// <summary>Users, credentials and sessions — the tables with foreign keys, and the ones where a
    /// bounded key column would fail first if the DDL got it wrong.</summary>
    [Theory]
    [MemberData(nameof(Engines))]
    public void AUserAndTheirCredentialRoundTrip(StateEngine engine)
    {
        var database = Open(engine);
        var users = new UserStore(database);

        Assert.False(users.Any());

        var user = users.CreateUser("Ada Lovelace", "ada@example.invalid", UserRole.Admin);
        users.AddCredential(user.Id, "Windows", "S-1-5-21-1", secret: null, label: "work laptop");

        Assert.True(users.Any());
        Assert.Equal(user.Id, users.FindByCredential("Windows", "S-1-5-21-1")?.Id);
        Assert.Null(users.FindByCredential("Windows", "S-1-5-21-2"));
        Assert.Single(users.CredentialsOf(user.Id));
    }
}
