using DbDataSync.Core.Config;
using DbDataSync.Drivers.Abstractions;
using DbDataSync.Drivers.Generic;
using Npgsql;
using Xunit;
using DbDataSync.Core.Sql;

namespace DbDataSync.Drivers.Postgres.Tests;

/// <summary>
/// The **same** reader as <c>DbDataSync.Drivers.MsSql.Tests.TriggerAuditReaderTests</c>, against a
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

    private const string MappingName = "trigger-audit-probe";

    /// <summary>Matches InitializeAsync's own CREATE TABLE — the key/non-key split this reader runs on
    /// as of phase 91 comes from here, never a live catalog call.</summary>
    private static List<CachedColumn> Columns() =>
    [
        new("Id", "int", false, true, false),
        new("Name", "varchar(50)", false, false, false),
    ];

    /// <summary>
    /// A null <paramref name="watermark"/> used to mean "read this reader's own first-pass full load".
    /// This reader no longer full-loads on <see cref="ReadIntent.InitialLoad"/> — <c>RunExecutor</c>
    /// routes that to the Bulk Load pipeline instead, capturing the position ahead of it (see
    /// <see cref="AnInitialLoad_CapturesThePosition_AndDoesNotFullLoad"/>) — so a null watermark here
    /// captures that same position instead and wraps it in an equivalent (empty) <see cref="ReadResult"/>.
    /// Every other call site only ever wanted this call's *position*, never its rows, so they are
    /// unaffected.
    /// </summary>
    private async Task<ReadResult> ReadAsync(string? watermark, IReadOnlyDictionary<string, string>? options = null)
    {
        if (watermark is null)
        {
            var capturing = Assert.IsAssignableFrom<IPositionCapturing>(_reader);
            var captured = await capturing.CapturePositionAsync(
                _connection, Source(), options ?? new Dictionary<string, string>(), CancellationToken.None);
            return new ReadResult(EmptyRows(), captured.Position, new ReadDiagnostics());
        }

        return await _reader.ReadChangesAsync(
            _connection, Source(), watermark, ReadIntent.Changes,
            [], MappingName, Columns(), [],new Dictionary<string, IReadOnlyList<CachedColumn>>(), options ?? new Dictionary<string, string>(),
            CancellationToken.None);
    }

    private static async IAsyncEnumerable<ChangeRow> EmptyRows()
    {
        await Task.CompletedTask;
        yield break;
    }

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

    /// <summary>
    /// The correctness crux phase 134 depends on. This reader no longer full-loads on
    /// <see cref="ReadIntent.InitialLoad"/> — <c>RunExecutor</c> routes that to the Bulk Load pipeline
    /// instead, capturing this reader's position (<see cref="IPositionCapturing.CapturePositionAsync"/>)
    /// before the load ever reads a row. A change committed *after* the position was captured but
    /// before anything reads from it must still arrive on the very next <see cref="ReadIntent.Changes"/>
    /// pass — get the ordering backwards and this is exactly the row that goes missing, silently.
    /// </summary>
    [Fact]
    public async Task AnInitialLoad_CapturesThePosition_AndDoesNotFullLoad()
    {
        await ExecuteAsync($"INSERT INTO public.\"{_tableName}\" (\"Id\", \"Name\") VALUES (1, 'Alice'), (2, 'Bob');");

        var capturing = Assert.IsAssignableFrom<IPositionCapturing>(_reader);
        var captured = await capturing.CapturePositionAsync(
            _connection, Source(), new Dictionary<string, string>(), CancellationToken.None);
        Assert.False(string.IsNullOrEmpty(captured.Position));

        await ExecuteAsync($"INSERT INTO public.\"{_tableName}\" (\"Id\", \"Name\") VALUES (3, 'Carol');");

        var rows = await CollectAsync((await ReadAsync(captured.Position)).Rows);

        var inserted = Assert.Single(rows);
        Assert.Equal(ChangeOperation.Insert, inserted.Operation);
        Assert.Equal(3, (int)inserted["Id"]!);
        Assert.Equal("Carol", (string)inserted["Name"]!);
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
        List<CachedColumn> compositeColumns =
        [
            new("Region", "varchar(10)", false, true, false),
            new("Id", "int", false, true, false),
            new("Name", "varchar(50)", false, false, false),
        ];
        // A null watermark used to mean "read this reader's own first-pass full load" — see the
        // file-level ReadAsync's own doc comment. This test needs its composite-key column shape rather
        // than that helper's fixed Columns(), so it repeats the same capture-instead-of-full-load fix
        // inline rather than generalizing ReadAsync for one caller.
        async Task<ReadResult> Read(string? watermark)
        {
            if (watermark is null)
            {
                var capturing = Assert.IsAssignableFrom<IPositionCapturing>(_reader);
                var captured = await capturing.CapturePositionAsync(
                    _connection, source, new Dictionary<string, string>(), CancellationToken.None);
                return new ReadResult(EmptyRows(), captured.Position, new ReadDiagnostics());
            }

            return await _reader.ReadChangesAsync(
                _connection, source, watermark, ReadIntent.Changes,
                [], MappingName, compositeColumns, [],new Dictionary<string, IReadOnlyList<CachedColumn>>(), new Dictionary<string, string>(),
                CancellationToken.None);
        }

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
