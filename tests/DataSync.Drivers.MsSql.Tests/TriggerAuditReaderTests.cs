using DataSync.Core.Config;
using DataSync.Drivers.Abstractions;
using DataSync.Drivers.Generic;
using Microsoft.Data.SqlClient;
using Xunit;

namespace DataSync.Drivers.MsSql.Tests;

/// <summary>
/// The generic trigger-audit reader against SQL Server. The Postgres tests run the *same* assertions
/// through the same reader — that is the claim this mechanism is built on, and running the body twice
/// against two engines is the only way to hold it.
/// </summary>
[Trait("Category", "Integration")]
public sealed class TriggerAuditReaderTests(MsSqlTestDatabase db) : IClassFixture<MsSqlTestDatabase>, IAsyncLifetime
{
    private readonly TriggerAuditReader _reader = new(MsSqlDialect.Instance, MsSqlCatalog.Instance);
    private SqlConnection _connection = null!;
    private string _tableName = null!;

    public async Task InitializeAsync()
    {
        _connection = db.OpenConnection();
        _tableName = $"TrgProbe_{Guid.NewGuid():N}";

        await ExecuteAsync($"""
            CREATE TABLE dbo.[{_tableName}] (
                Id INT NOT NULL PRIMARY KEY,
                Name NVARCHAR(50) NOT NULL
            );
            """);

        await ExecuteAsync(MsSqlTriggerAudit.CreateShadowTable(
            "dbo", _tableName, ["[Id] INT NOT NULL"]));
        await ExecuteAsync(MsSqlTriggerAudit.CreateTrigger("dbo", _tableName, ["Id"]));
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
        ConnectionName = "test", Database = db.DatabaseName, Schema = "dbo", Table = _tableName,
    };

    private Task<ReadResult> ReadAsync(string? watermark, IReadOnlyDictionary<string, string>? options = null) =>
        _reader.ReadChangesAsync(
            _connection, Source(), watermark, [], options ?? new Dictionary<string, string>(), CancellationToken.None);

    private static async Task<List<ChangeRow>> CollectAsync(IAsyncEnumerable<ChangeRow> rows)
    {
        var list = new List<ChangeRow>();
        await foreach (var row in rows)
            list.Add(row);
        return list;
    }

    private async Task<long> ShadowRowCountAsync()
    {
        await using var cmd = _connection.CreateCommand();
        cmd.CommandText = $"SELECT COUNT(*) FROM dbo.[{TriggerAuditStatement.ShadowTableName(_tableName)}];";
        return Convert.ToInt64(await cmd.ExecuteScalarAsync());
    }

    /// <summary>The shadow table holds only what happened since the trigger was created, so a table
    /// that already had rows would otherwise start half-replicated with nothing to say so.</summary>
    [Fact]
    public async Task WithNoStoredPosition_EveryRowIsReadAsAnInsert()
    {
        await ExecuteAsync($"INSERT INTO dbo.[{_tableName}] (Id, Name) VALUES (1, 'Alice'), (2, 'Bob');");

        var rows = await CollectAsync((await ReadAsync(null)).Rows);

        Assert.Equal(2, rows.Count);
        Assert.All(rows, r => Assert.Equal(ChangeOperation.Insert, r.Operation));
    }

    [Fact]
    public async Task EachOperation_ComesBackAsItself()
    {
        await ExecuteAsync($"INSERT INTO dbo.[{_tableName}] (Id, Name) VALUES (1, 'Alice'), (2, 'Bob');");
        var start = (await ReadAsync(null)).NewWatermark;

        await ExecuteAsync($"INSERT INTO dbo.[{_tableName}] (Id, Name) VALUES (3, 'Carol');");
        await ExecuteAsync($"UPDATE dbo.[{_tableName}] SET Name = 'Robert' WHERE Id = 2;");
        await ExecuteAsync($"DELETE FROM dbo.[{_tableName}] WHERE Id = 1;");

        var rows = await CollectAsync((await ReadAsync(start)).Rows);

        Assert.Equal(3, rows.Count);
        Assert.Equal(ChangeOperation.Insert, Assert.Single(rows, r => (int)r["Id"]! == 3).Operation);
        Assert.Equal(ChangeOperation.Update, Assert.Single(rows, r => (int)r["Id"]! == 2).Operation);
        Assert.Equal(ChangeOperation.Delete, Assert.Single(rows, r => (int)r["Id"]! == 1).Operation);
    }

    /// <summary>
    /// The phase 12 bug, which this mechanism has the same shape of. A delete's row is gone from the
    /// base table, so the LEFT JOIN yields nothing — and if the key came from there it would be NULL,
    /// destroying the one value still reliable. This fails if the select list ever takes the key from
    /// `base`.
    /// </summary>
    [Fact]
    public async Task ADeletedRowKeepsItsKey()
    {
        await ExecuteAsync($"INSERT INTO dbo.[{_tableName}] (Id, Name) VALUES (7, 'Gone');");
        var start = (await ReadAsync(null)).NewWatermark;

        await ExecuteAsync($"DELETE FROM dbo.[{_tableName}] WHERE Id = 7;");

        var row = Assert.Single(await CollectAsync((await ReadAsync(start)).Rows));

        Assert.Equal(ChangeOperation.Delete, row.Operation);
        Assert.Equal(7, row["Id"]);
    }

    /// <summary>
    /// The difference between usable on a busy table and not: a shadow table records every write, so
    /// fifty updates to one row are fifty shadow rows and one row to write.
    /// </summary>
    [Fact]
    public async Task ManyUpdatesToOneKey_CollapseToOneRow()
    {
        await ExecuteAsync($"INSERT INTO dbo.[{_tableName}] (Id, Name) VALUES (1, 'a');");
        var start = (await ReadAsync(null)).NewWatermark;

        for (var i = 0; i < 50; i++)
            await ExecuteAsync($"UPDATE dbo.[{_tableName}] SET Name = 'v{i}' WHERE Id = 1;");

        var rows = await CollectAsync((await ReadAsync(start)).Rows);

        var row = Assert.Single(rows);
        Assert.Equal("v49", row["Name"]);
        Assert.Equal(51, await ShadowRowCountAsync());
    }

    /// <summary>Bounded at both ends, so reading twice with nothing in between reports nothing the
    /// second time rather than replaying the last change.</summary>
    [Fact]
    public async Task ReadingTwiceWithNoChangeInBetween_ReturnsNothingTheSecondTime()
    {
        await ExecuteAsync($"INSERT INTO dbo.[{_tableName}] (Id, Name) VALUES (1, 'Alice');");
        var start = (await ReadAsync(null)).NewWatermark;

        await ExecuteAsync($"INSERT INTO dbo.[{_tableName}] (Id, Name) VALUES (2, 'Bob');");
        var first = await ReadAsync(start);
        Assert.Single(await CollectAsync(first.Rows));

        var second = await ReadAsync(first.NewWatermark);
        Assert.Empty(await CollectAsync(second.Rows));
    }

    /// <summary>Off by default: pruning is a delete against somebody's source database, and doing it
    /// unasked is not this tool's call.</summary>
    [Fact]
    public async Task WithoutTheOption_AcknowledgementPrunesNothing()
    {
        await ExecuteAsync($"INSERT INTO dbo.[{_tableName}] (Id, Name) VALUES (1, 'Alice');");
        var watermark = (await ReadAsync(null)).NewWatermark;

        await _reader.AcknowledgeAsync(
            _connection, Source(), watermark, new Dictionary<string, string>(), CancellationToken.None);

        Assert.Equal(1, await ShadowRowCountAsync());
    }

    /// <summary>The shadow table grows forever unless something deletes below the watermark, and the
    /// watermark lives in DataSync's state store rather than the source — so acknowledgement is what
    /// carries it back.</summary>
    [Fact]
    public async Task WithTheOption_AcknowledgementPrunesWhatHasBeenApplied()
    {
        var options = new Dictionary<string, string> { [TriggerAuditReader.PruneOption] = "true" };

        await ExecuteAsync($"INSERT INTO dbo.[{_tableName}] (Id, Name) VALUES (1, 'Alice'), (2, 'Bob');");
        var start = (await ReadAsync(null, options)).NewWatermark;
        await _reader.AcknowledgeAsync(_connection, Source(), start, options, CancellationToken.None);

        Assert.Equal(0, await ShadowRowCountAsync());

        // And a change written after the acknowledged position is untouched by it.
        await ExecuteAsync($"UPDATE dbo.[{_tableName}] SET Name = 'Robert' WHERE Id = 2;");
        Assert.Equal(1, await ShadowRowCountAsync());
        Assert.Single(await CollectAsync((await ReadAsync(start, options)).Rows));
    }

    /// <summary>Without a key there is nothing to collapse changes by and nothing to identify a
    /// deleted row with, so it is refused where it can be explained.</summary>
    [Fact]
    public async Task ATableWithNoPrimaryKey_IsRefusedWithAReason()
    {
        var keyless = $"NoKey_{Guid.NewGuid():N}";
        await ExecuteAsync($"CREATE TABLE dbo.[{keyless}] (Id INT NOT NULL);");

        // Given a shadow table, so the failure under test is the missing key rather than the missing
        // shadow table — which the reader would otherwise report first, correctly.
        await ExecuteAsync(MsSqlTriggerAudit.CreateShadowTable("dbo", keyless, ["[Id] INT NOT NULL"]));

        // And a change in it, so the reader gets as far as building the read. An empty shadow table
        // is "nothing to do", which is a correct answer and not the one under test.
        await ExecuteAsync(
            $"INSERT INTO dbo.[{TriggerAuditStatement.ShadowTableName(keyless)}] ([DS_Op], [Id]) VALUES ('I', 1);");

        var source = new SourceTableRef
        {
            ConnectionName = "test", Database = db.DatabaseName, Schema = "dbo", Table = keyless,
        };

        var problem = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            var result = await _reader.ReadChangesAsync(
                _connection, source, "0", [], new Dictionary<string, string>(), CancellationToken.None);
            await CollectAsync(result.Rows);
        });

        Assert.Contains("no primary key", problem.Message);
    }

    /// <summary>A table nobody has enabled capture on is a configuration answer, not a crash — and it
    /// says where to turn it on.</summary>
    [Fact]
    public async Task ATableWithNoShadowTable_SaysWhereToEnableIt()
    {
        var uncaptured = $"NoShadow_{Guid.NewGuid():N}";
        await ExecuteAsync($"CREATE TABLE dbo.[{uncaptured}] (Id INT NOT NULL PRIMARY KEY);");

        var source = new SourceTableRef
        {
            ConnectionName = "test", Database = db.DatabaseName, Schema = "dbo", Table = uncaptured,
        };

        var problem = await Assert.ThrowsAsync<InvalidOperationException>(() => _reader.ReadChangesAsync(
            _connection, source, null, [], new Dictionary<string, string>(), CancellationToken.None));

        Assert.Contains("Setup card", problem.Message);
    }
}
