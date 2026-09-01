using Microsoft.Data.SqlClient;
using Xunit;

namespace DataSync.Drivers.MsSql.Tests;

/// <summary>
/// That Change Tracking will state, itself, when a version committed — the claim phase 85's exact
/// Change Tracking lag rests on, and the one that turned that figure from an estimate into a fact.
/// <para>
/// The claim is that a <c>SYS_CHANGE_VERSION</c> is a commit sequence number, so
/// <c>sys.dm_tran_commit_table.commit_ts</c> is keyed by the same counter and the join is sound. That
/// is either true of a real server or it is not true at all; there is nothing to fake here that would
/// still be testing it.
/// </para>
/// <para>
/// Against the shared fixture database, which is already <c>CHANGE_TRACKING = ON</c> — the DMV is
/// database-scoped and populated by committing to any tracked table, which keeps this off the
/// per-table setup <c>MsSqlChangeTrackingReaderTests</c> needs beyond one table of its own to commit
/// into.
/// </para>
/// </summary>
[Trait("Category", "Integration")]
public sealed class MsSqlChangeTrackingVersionTimeTests(MsSqlTestDatabase db)
    : IClassFixture<MsSqlTestDatabase>, IAsyncLifetime
{
    private SqlConnection _connection = null!;
    private string _tableName = null!;

    public async Task InitializeAsync()
    {
        _connection = db.OpenConnection();
        _tableName = $"CtCommitTime_{Guid.NewGuid():N}";

        await ExecuteAsync($"""
            CREATE TABLE dbo.[{_tableName}] (
                Id INT NOT NULL PRIMARY KEY,
                Name NVARCHAR(50) NOT NULL
            );
            """);
        await ExecuteAsync($"ALTER TABLE dbo.[{_tableName}] ENABLE CHANGE_TRACKING;");
    }

    public Task DisposeAsync()
    {
        _connection.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task ARecentVersion_MapsToTheTimeItsCommitHappened()
    {
        // A commit of our own, so there is a version this database has certainly just produced and
        // whose row the DMV cannot yet have aged out.
        await ExecuteAsync($"INSERT INTO dbo.[{_tableName}] (Id, Name) VALUES (1, 'one');");
        var version = await MsSqlChangeTrackingReader.GetCurrentVersionAsync(
            _connection, CancellationToken.None);

        var mapped = await MsSqlChangeTrackingReader.MapVersionToTimeAsync(
            _connection, version, CancellationToken.None);

        // A real time, not a null and not a guess — which is the entire difference between this
        // figure and the polling-history estimate it replaced. Loosely bounded on purpose: the value
        // is the source server's own clock, usable as one end of a difference against another value
        // from this same view and not as an absolute against ours.
        Assert.NotNull(mapped);
        Assert.InRange(
            mapped.Value.DateTime,
            DateTime.UtcNow.AddDays(-1),
            DateTime.UtcNow.AddDays(1));
    }

    [Fact]
    public async Task TwoVersionsInOrder_MapToTimesInThatOrder()
    {
        await ExecuteAsync($"INSERT INTO dbo.[{_tableName}] (Id, Name) VALUES (10, 'first');");
        var earlier = await MsSqlChangeTrackingReader.GetCurrentVersionAsync(
            _connection, CancellationToken.None);

        await Task.Delay(TimeSpan.FromSeconds(1));

        await ExecuteAsync($"INSERT INTO dbo.[{_tableName}] (Id, Name) VALUES (11, 'second');");
        var later = await MsSqlChangeTrackingReader.GetCurrentVersionAsync(
            _connection, CancellationToken.None);

        Assert.True(later > earlier);

        var earlierTime = await MsSqlChangeTrackingReader.MapVersionToTimeAsync(
            _connection, earlier, CancellationToken.None);
        var laterTime = await MsSqlChangeTrackingReader.MapVersionToTimeAsync(
            _connection, later, CancellationToken.None);

        // The property lag actually depends on, rather than just that a time came back: subtracting
        // one of these from the other has to yield a duration with the sign the versions imply. A
        // mapping that returned times unrelated to version order would still pass the test above and
        // would make every lag figure nonsense.
        Assert.NotNull(earlierTime);
        Assert.NotNull(laterTime);
        Assert.True(laterTime >= earlierTime);
    }

    [Fact]
    public async Task AVersionTheViewDoesNotHold_IsNullRatherThanAnError()
    {
        await ExecuteAsync($"INSERT INTO dbo.[{_tableName}] (Id, Name) VALUES (20, 'twenty');");
        var current = await MsSqlChangeTrackingReader.GetCurrentVersionAsync(
            _connection, CancellationToken.None);

        // A version the view has no row for, which in production is a version old enough to have
        // aged out of its rolling window: the "too old to place" answer that hands the figure over
        // to the polling-history estimate. An exception here instead of a null would fail a status
        // screen over a mapping that is merely far behind — the mapping most worth reporting on.
        //
        // **Reached from above rather than from below, because this fixture cannot age anything
        // out.** MsSqlTestDatabase sets CHANGE_RETENTION = 2 DAYS with AUTO_CLEANUP = OFF, so even
        // commit_ts 1 is still in the view on a database this young — asserting on an old version
        // here tests the fixture's retention settings, not the code. What the code does with a
        // version the view will not place is identical either way: no row, so no time.
        var mapped = await MsSqlChangeTrackingReader.MapVersionToTimeAsync(
            _connection, current + 1_000_000, CancellationToken.None);

        Assert.Null(mapped);
    }

    private async Task ExecuteAsync(string sql)
    {
        await using var cmd = _connection.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }
}
