using DataSync.Core.Config;
using DataSync.Drivers.Abstractions;
using DataSync.Drivers.Generic;
using Npgsql;
using Xunit;
using DataSync.Core.Sql;

namespace DataSync.Drivers.Postgres.Tests;

/// <summary>
/// The **same** reader as <c>DataSync.Drivers.MsSql.Tests.TriggerAuditReaderTests</c>, against a
/// different engine, asserting the same things. That is the whole argument for this mechanism: one
/// reader implementation delivers delete detection to every engine that has triggers, where every
/// log-based mechanism is one engine's and needs its own provider, privilege and server setting.
/// <para>
/// Only the setup differs — Postgres has no inline trigger body, so it takes a plpgsql function and a
/// trigger that calls it, where SQL Server takes one statement-level trigger. That divergence is
/// exactly why the DDL is per-engine and the read is not.
/// </para>
/// </summary>
[Trait("Category", "Integration")]
public sealed class TriggerAuditReaderTests(PostgresTestDatabase db) : IClassFixture<PostgresTestDatabase>, IAsyncLifetime
{
    private readonly TriggerAuditReader _reader = new(PostgresDialect.Instance, PostgresCatalog.Instance);
    private NpgsqlConnection _connection = null!;
    private string _tableName = null!;

    public async Task InitializeAsync()
    {
        _connection = db.OpenConnection();
        _tableName = $"trg_probe_{Guid.NewGuid():N}";

        await ExecuteAsync($"""
            CREATE TABLE public."{_tableName}" (
                "Id" INT NOT NULL PRIMARY KEY,
                "Name" VARCHAR(50) NOT NULL
            );
            """);

        await ExecuteAsync(PostgresTriggerAudit.CreateShadowTable(
            "public", _tableName, ["\"Id\" INT NOT NULL"]));
        await ExecuteAsync(PostgresTriggerAudit.CreateFunctionAndTrigger("public", _tableName, ["Id"]));
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
        ConnectionName = "test", Database = db.DatabaseName, Schema = "public", Table = _tableName,
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
        cmd.CommandText = $"SELECT COUNT(*) FROM public.\"{TriggerAuditStatement.ShadowTableName(_tableName)}\";";
        return Convert.ToInt64(await cmd.ExecuteScalarAsync());
    }

    [Fact]
    public async Task WithNoStoredPosition_EveryRowIsReadAsAnInsert()
    {
        await ExecuteAsync($"INSERT INTO public.\"{_tableName}\" (\"Id\", \"Name\") VALUES (1, 'Alice'), (2, 'Bob');");

        var rows = await CollectAsync((await ReadAsync(null)).Rows);

        Assert.Equal(2, rows.Count);
        Assert.All(rows, r => Assert.Equal(ChangeOperation.Insert, r.Operation));
    }

    [Fact]
    public async Task EachOperation_ComesBackAsItself()
    {
        await ExecuteAsync($"INSERT INTO public.\"{_tableName}\" (\"Id\", \"Name\") VALUES (1, 'Alice'), (2, 'Bob');");
        var start = (await ReadAsync(null)).NewWatermark;

        await ExecuteAsync($"INSERT INTO public.\"{_tableName}\" (\"Id\", \"Name\") VALUES (3, 'Carol');");
        await ExecuteAsync($"UPDATE public.\"{_tableName}\" SET \"Name\" = 'Robert' WHERE \"Id\" = 2;");
        await ExecuteAsync($"DELETE FROM public.\"{_tableName}\" WHERE \"Id\" = 1;");

        var rows = await CollectAsync((await ReadAsync(start)).Rows);

        Assert.Equal(3, rows.Count);
        Assert.Equal(ChangeOperation.Insert, Assert.Single(rows, r => (int)r["Id"]! == 3).Operation);
        Assert.Equal(ChangeOperation.Update, Assert.Single(rows, r => (int)r["Id"]! == 2).Operation);
        Assert.Equal(ChangeOperation.Delete, Assert.Single(rows, r => (int)r["Id"]! == 1).Operation);
    }

    /// <summary>The phase 12 bug's shape, on the other engine — a delete's key comes from the shadow
    /// row, because the base row is gone and the LEFT JOIN would make it NULL.</summary>
    [Fact]
    public async Task ADeletedRowKeepsItsKey()
    {
        await ExecuteAsync($"INSERT INTO public.\"{_tableName}\" (\"Id\", \"Name\") VALUES (7, 'Gone');");
        var start = (await ReadAsync(null)).NewWatermark;

        await ExecuteAsync($"DELETE FROM public.\"{_tableName}\" WHERE \"Id\" = 7;");

        var row = Assert.Single(await CollectAsync((await ReadAsync(start)).Rows));

        Assert.Equal(ChangeOperation.Delete, row.Operation);
        Assert.Equal(7, row["Id"]);
    }

    [Fact]
    public async Task ManyUpdatesToOneKey_CollapseToOneRow()
    {
        await ExecuteAsync($"INSERT INTO public.\"{_tableName}\" (\"Id\", \"Name\") VALUES (1, 'a');");
        var start = (await ReadAsync(null)).NewWatermark;

        for (var i = 0; i < 50; i++)
            await ExecuteAsync($"UPDATE public.\"{_tableName}\" SET \"Name\" = 'v{i}' WHERE \"Id\" = 1;");

        var row = Assert.Single(await CollectAsync((await ReadAsync(start)).Rows));

        Assert.Equal("v49", row["Name"]);
        Assert.Equal(51, await ShadowRowCountAsync());
    }

    [Fact]
    public async Task WithTheOption_AcknowledgementPrunesWhatHasBeenApplied()
    {
        var options = new Dictionary<string, string> { [TriggerAuditReader.PruneOption] = "true" };

        await ExecuteAsync($"INSERT INTO public.\"{_tableName}\" (\"Id\", \"Name\") VALUES (1, 'Alice');");
        var start = (await ReadAsync(null, options)).NewWatermark;
        await _reader.AcknowledgeAsync(_connection, Source(), start, options, CancellationToken.None);

        Assert.Equal(0, await ShadowRowCountAsync());
    }

    /// <summary>
    /// A composite key, because the shadow table carries the key columns and a composite one is
    /// several of them — the sequence orders the feed, not the key, so this should fall out. Worth a
    /// test rather than an assumption, which is what the phase doc said.
    /// </summary>
    [Fact]
    public async Task ACompositeKey_CollapsesAndIdentifiesADeleteByEveryPart()
    {
        var composite = $"trg_composite_{Guid.NewGuid():N}";
        await ExecuteAsync($"""
            CREATE TABLE public."{composite}" (
                "Region" VARCHAR(10) NOT NULL,
                "Id" INT NOT NULL,
                "Name" VARCHAR(50) NOT NULL,
                PRIMARY KEY ("Region", "Id")
            );
            """);
        await ExecuteAsync(PostgresTriggerAudit.CreateShadowTable(
            "public", composite, ["\"Region\" VARCHAR(10) NOT NULL", "\"Id\" INT NOT NULL"]));
        await ExecuteAsync(PostgresTriggerAudit.CreateFunctionAndTrigger("public", composite, ["Region", "Id"]));

        var source = new SourceTableRef
        {
            ConnectionName = "test", Database = db.DatabaseName, Schema = "public", Table = composite,
        };
        Task<ReadResult> Read(string? watermark) => _reader.ReadChangesAsync(
            _connection, source, watermark, [], new Dictionary<string, string>(), CancellationToken.None);

        await ExecuteAsync(
            $"INSERT INTO public.\"{composite}\" VALUES ('north', 1, 'a'), ('south', 1, 'b');");
        var start = (await Read(null)).NewWatermark;

        await ExecuteAsync($"UPDATE public.\"{composite}\" SET \"Name\" = 'c' WHERE \"Region\" = 'north';");
        await ExecuteAsync($"UPDATE public.\"{composite}\" SET \"Name\" = 'd' WHERE \"Region\" = 'north';");
        await ExecuteAsync($"DELETE FROM public.\"{composite}\" WHERE \"Region\" = 'south';");

        var rows = await CollectAsync((await Read(start)).Rows);

        Assert.Equal(2, rows.Count);
        var updated = Assert.Single(rows, r => r.Operation == ChangeOperation.Update);
        Assert.Equal("north", updated["Region"]);
        Assert.Equal("d", updated["Name"]);

        // Both halves of the key survive a delete, which is the thing a single-column test cannot say.
        var deleted = Assert.Single(rows, r => r.Operation == ChangeOperation.Delete);
        Assert.Equal("south", deleted["Region"]);
        Assert.Equal(1, deleted["Id"]);
    }
}
