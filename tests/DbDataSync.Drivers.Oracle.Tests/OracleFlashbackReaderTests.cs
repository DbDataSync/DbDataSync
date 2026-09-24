using DbDataSync.Core.Config;
using DbDataSync.Drivers.Abstractions;
using DbDataSync.Drivers.Generic;
using Oracle.ManagedDataAccess.Client;
using DbDataSync.Core.Sql;

namespace DbDataSync.Drivers.Oracle.Tests;

/// <summary>
/// Requires <c>docker/oracle-init/10-grants.sql</c>'s <c>EXECUTE ON DBMS_FLASHBACK</c> grant to have
/// actually run — confirmed by testing that it fails outright (<c>ORA-00904</c>) without it, which is
/// exactly why that grant exists and is checked in rather than left as a manual setup step.
/// <para>
/// Runs against <c>docker/oracle-init/20-flashback-probe-table.sql</c>'s pre-provisioned
/// <c>fb_probe_shared</c> table, **not** a table created fresh per test — confirmed by extensive live
/// testing that Oracle's Flashback Version Query cannot produce <c>VERSIONS BETWEEN SCN</c> history for
/// a table whose own <c>CREATE TABLE</c> is very recent (<c>ORA-01466</c>, "table definition has
/// changed"), even when the requested SCN window falls entirely after creation. Bind vs. literal SCN
/// bounds, a delay up to 90 seconds, a same-table DML warm-up, a fresh connection, no primary
/// key/index, and explicit <c>COMMIT</c>s were all ruled out individually — only real table age (a
/// table that existed before this test *process* started) made the difference. Each test cleans its own
/// rows out rather than the table being dropped and recreated.
/// </para>
/// </summary>
[Trait("Category", "Integration")]
public sealed class OracleFlashbackReaderTests(OracleTestDatabase db) : IClassFixture<OracleTestDatabase>, IAsyncLifetime
{
    // Uppercase to match the unquoted CREATE TABLE in docker/oracle-init/20-flashback-probe-table.sql,
    // which Oracle folds to FB_PROBE_SHARED — this driver's own dialect.QualifyTable quotes whatever
    // case it's given, so a lowercase reference here would look for a table that doesn't exist.
    private const string TableName = "FB_PROBE_SHARED";

    private readonly OracleFlashbackReader _reader = new(OracleDialect.Instance);
    private OracleConnection _connection = null!;

    public async Task InitializeAsync()
    {
        _connection = db.OpenConnection();
        await ExecuteAsync($"DELETE FROM {TableName}");
    }

    public Task DisposeAsync()
    {
        _connection.Dispose();
        return Task.CompletedTask;
    }

    private async Task ExecuteAsync(string sql)
    {
        await using var cmd = _connection.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }

    private SourceTableRef Source() => new()
    {
        ConnectionName = "test", Database = "FREEPDB1", Schema = db.SchemaName, Table = TableName,
    };

    private static List<ColumnMapping> Mappings() =>
    [
        new() { SourceColumn = "ID", TargetColumn = "ID" },
        new() { SourceColumn = "NAME", TargetColumn = "NAME" },
    ];

    private static async Task<List<ChangeRow>> CollectAsync(IAsyncEnumerable<ChangeRow> rows)
    {
        var list = new List<ChangeRow>();
        await foreach (var row in rows)
            list.Add(row);
        return list;
    }

    [Fact]
    public void DetectsDeletes_IsTrue() => Assert.True(_reader.DetectsDeletes);

    [Fact]
    public async Task CapturePositionAsync_ReturnsAPositiveIncreasingScn()
    {
        var capturing = Assert.IsAssignableFrom<IPositionCapturing>(_reader);
        var first = await capturing.CapturePositionAsync(_connection, Source(), new Dictionary<string, string>(), CancellationToken.None);
        Assert.True(long.Parse(first.Position) > 0);

        await ExecuteAsync($"INSERT INTO {TableName} VALUES (1, 'Alice')");

        var second = await capturing.CapturePositionAsync(_connection, Source(), new Dictionary<string, string>(), CancellationToken.None);
        Assert.True(long.Parse(second.Position) >= long.Parse(first.Position));
    }

    /// <summary>
    /// Unlike <c>TriggerAuditReader</c>, Flashback does not collapse multiple operations on one key
    /// into a net change — every committed version is its own row. Id 2's insert and its later update
    /// are two separate version rows, not one; the count below (4, not 3) is what genuinely correct
    /// behavior produces, confirmed by first writing the wrong expectation (a reflexive copy of
    /// TriggerAuditReader's own collapsing test) and having the real driver disagree with it.
    /// </summary>
    [Fact]
    public async Task EachOperation_ComesBackAsItself()
    {
        var capturing = Assert.IsAssignableFrom<IPositionCapturing>(_reader);
        var start = (await capturing.CapturePositionAsync(_connection, Source(), new Dictionary<string, string>(), CancellationToken.None)).Position;

        await ExecuteAsync($"INSERT INTO {TableName} VALUES (1, 'Alice')");
        await ExecuteAsync($"INSERT INTO {TableName} VALUES (2, 'Bob')");
        await ExecuteAsync($"UPDATE {TableName} SET name = 'Robert' WHERE id = 2");
        await ExecuteAsync($"DELETE FROM {TableName} WHERE id = 1");

        var result = await _reader.ReadChangesAsync(
            _connection, Source(), start, ReadIntent.Changes, Mappings(), "flashback-probe", [], [],
            new Dictionary<string, string>(), CancellationToken.None);
        var rows = await CollectAsync(result.Rows);

        Assert.Equal(4, rows.Count);
        Assert.Single(rows, r => r.Operation == ChangeOperation.Insert && Convert.ToDecimal(r["ID"]) == 1m);
        Assert.Single(rows, r => r.Operation == ChangeOperation.Insert && Convert.ToDecimal(r["ID"]) == 2m);
        Assert.Single(rows, r => r.Operation == ChangeOperation.Update && Convert.ToDecimal(r["ID"]) == 2m);
        Assert.Single(rows, r => r.Operation == ChangeOperation.Delete && Convert.ToDecimal(r["ID"]) == 1m);
    }

    /// <summary>The genuine, positive divergence from TriggerAuditReader documented on the class
    /// itself: a Flashback delete's row is the version as it stood just before deletion, not a
    /// key-only row with every other column null.</summary>
    [Fact]
    public async Task ADeletedRow_CarriesItsFullValues_NotJustItsKey()
    {
        var capturing = Assert.IsAssignableFrom<IPositionCapturing>(_reader);
        await ExecuteAsync($"INSERT INTO {TableName} VALUES (9, 'Gone')");
        var start = (await capturing.CapturePositionAsync(_connection, Source(), new Dictionary<string, string>(), CancellationToken.None)).Position;

        await ExecuteAsync($"DELETE FROM {TableName} WHERE id = 9");

        var result = await _reader.ReadChangesAsync(
            _connection, Source(), start, ReadIntent.Changes, Mappings(), "flashback-probe", [], [],
            new Dictionary<string, string>(), CancellationToken.None);
        var row = Assert.Single(await CollectAsync(result.Rows));

        Assert.Equal(ChangeOperation.Delete, row.Operation);
        Assert.Equal(9m, Convert.ToDecimal(row["ID"]));
        Assert.Equal("Gone", row["NAME"]);
    }

    [Fact]
    public async Task ChangesFromLatest_AdoptsThePositionWithoutReadingAnyRow()
    {
        await ExecuteAsync($"INSERT INTO {TableName} VALUES (1, 'Alice')");

        var result = await _reader.ReadChangesAsync(
            _connection, Source(), previousWatermark: null, ReadIntent.ChangesFromLatest, Mappings(),
            "flashback-probe", [], [], new Dictionary<string, string>(), CancellationToken.None);

        Assert.Empty(await CollectAsync(result.Rows));
        Assert.True(long.Parse(result.NewWatermark) > 0);
    }

    [Fact]
    public async Task AlreadyCaughtUp_ReadsNothing()
    {
        var capturing = Assert.IsAssignableFrom<IPositionCapturing>(_reader);
        var start = (await capturing.CapturePositionAsync(_connection, Source(), new Dictionary<string, string>(), CancellationToken.None)).Position;

        var result = await _reader.ReadChangesAsync(
            _connection, Source(), start, ReadIntent.Changes, Mappings(), "flashback-probe", [], [],
            new Dictionary<string, string>(), CancellationToken.None);

        Assert.Empty(await CollectAsync(result.Rows));
    }

    [Fact]
    public void SupportedIntents_ExcludesChangesFromEarliest()
    {
        var declaring = Assert.IsAssignableFrom<IReadIntentDeclaring>(_reader);
        Assert.DoesNotContain(ReadIntent.ChangesFromEarliest, declaring.SupportedIntents);
        Assert.Contains(ReadIntent.Changes, declaring.SupportedIntents);
        Assert.Contains(ReadIntent.ChangesFromLatest, declaring.SupportedIntents);
    }
}
