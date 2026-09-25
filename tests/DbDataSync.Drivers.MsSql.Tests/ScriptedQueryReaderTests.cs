using DbDataSync.Core.Config;
using DbDataSync.Drivers.Abstractions;
using DbDataSync.Drivers.Generic;
using DbDataSync.Scripting;
using DbDataSync.Scripting.Abstractions;
using Microsoft.Data.SqlClient;
using Xunit;
using DbDataSync.Core.Sql;

namespace DbDataSync.Drivers.MsSql.Tests;

/// <summary>
/// A reader whose statement comes from a script, against a hand-rolled audit table — the shape a legacy
/// source actually has, and the case this Kind exists for. Inserts, updates **and deletes**, which is
/// the thing the built-in watermark reader cannot do.
/// </summary>
[Trait("Category", "Integration")]
public sealed class ScriptedQueryReaderTests(MsSqlTestDatabase db) : IClassFixture<MsSqlTestDatabase>, IAsyncLifetime
{
    private readonly string _repoRoot = Path.Combine(Path.GetTempPath(), $"ds-sq-{Guid.NewGuid():N}");
    private SqlConnection _connection = null!;
    private ConfigRepository _configRepository = null!;
    private ScriptedQueryReader _reader = null!;
    private string _table = null!;
    private string _audit = null!;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_repoRoot);
        LibGit2Sharp.Repository.Init(_repoRoot);
        _configRepository = new ConfigRepository(
            Path.Combine(_repoRoot, "config"),
            new Core.Git.GitCommitService(_repoRoot),
            new ClrKernel.Core.Secrets.SecretStore(true));

        var host = new ScriptHost(
            _configRepository, new ScriptCompiler(new ScriptCacheDirectory(Path.Combine(_repoRoot, "cache"))));
        _reader = new ScriptedQueryReader(host, MsSqlDialect.Instance, "MsSql", MsSqlCatalog.Instance);

        _connection = db.OpenConnection();
        var suffix = Guid.NewGuid().ToString("N");
        _table = $"Sq_{suffix}";
        _audit = $"SqAudit_{suffix}";

        await ExecuteAsync($"""
            CREATE TABLE dbo.[{_table}] (Id INT NOT NULL PRIMARY KEY, Name NVARCHAR(50) NOT NULL);
            CREATE TABLE dbo.[{_audit}] (
                Seq BIGINT IDENTITY(1,1) PRIMARY KEY,
                Op CHAR(1) NOT NULL,
                Id INT NOT NULL,
                Name NVARCHAR(50) NULL);
            """);
    }

    public Task DisposeAsync()
    {
        _connection.Dispose();
        try { Directory.Delete(_repoRoot, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        return Task.CompletedTask;
    }

    private async Task ExecuteAsync(string sql)
    {
        await using var cmd = _connection.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }

    private SourceTableRef Source() =>
        new() { ConnectionName = "src", Database = db.DatabaseName, Schema = "dbo", Table = _table };

    private static async Task<List<ChangeRow>> CollectAsync(IAsyncEnumerable<ChangeRow> rows)
    {
        var list = new List<ChangeRow>();
        await foreach (var row in rows)
            list.Add(row);
        return list;
    }

    /// <summary>
    /// The audit table's own name is a script parameter, taken from the reader's options — a scripted
    /// query is a pipeline-stage choice, so its script and that script's inputs are bound the way every
    /// other stage option is.
    /// </summary>
    private const string AuditQuery = """
        using System.Collections.Generic;
        using DbDataSync.Drivers.Abstractions;
        using DbDataSync.Scripting.Abstractions;

        public sealed class AuditQuery : ISourceQueryBuilder
        {
            public SourceQuery? BuildWatermarkQuery(SourceQueryContext c) =>
                SourceQuery.Text($"SELECT MAX(Seq) FROM dbo.{c.Dialect.QuoteIdentifier(c.Parameters.Require("auditTable"))};");

            public SourceQuery BuildReadQuery(SourceQueryContext c)
            {
                var audit = c.Dialect.QuoteIdentifier(c.Parameters.Require("auditTable"));
                var since = c.PreviousWatermark ?? "0";
                return new SourceQuery(
                    $@"SELECT Seq, Op, Id, Name FROM dbo.{audit}
                       WHERE Seq > {c.Dialect.ParameterReference("since")}
                         AND Seq <= {c.Dialect.ParameterReference("upto")}
                       ORDER BY Seq;",
                    new[]
                    {
                        new ScriptQueryParameter("since", long.Parse(since)),
                        new ScriptQueryParameter("upto", long.Parse(c.EndWatermark ?? since)),
                    });
            }

            public SourceQueryShape DescribeResult(SourceQueryContext c) => new(
                OperationColumn: "Op",
                OperationValues: new Dictionary<string, ChangeOperation>
                {
                    ["I"] = ChangeOperation.Insert,
                    ["U"] = ChangeOperation.Update,
                    ["D"] = ChangeOperation.Delete,
                },
                ExcludeColumns: new[] { "Seq" });
        }
        """;

    private void RegisterScript() =>
        _configRepository.SaveScript(
            new ScriptDefinition
            {
                Manifest = new ScriptConfig { Name = "audit-query", Kind = ScriptSlots.SqlColumnExpression, EntryType = "AuditQuery" },
                Code = AuditQuery,
            },
            new Core.Git.GitAuthor("t", "t@t"));

    private Dictionary<string, string> Options() => new()
    {
        [ScriptedQueryReader.ScriptOption] = "audit-query",
        ["auditTable"] = _audit,
    };

    private Task<ReadResult> ReadAsync(string? watermark) =>
        _reader.ReadChangesAsync(_connection, Source(), watermark, ReadIntent.InitialLoad, [], "mapping", [], [],new Dictionary<string, IReadOnlyList<CachedColumn>>(), Options(), CancellationToken.None);

    [Fact]
    public async Task ReadsInsertsUpdatesAndDeletesFromAnAuditTable()
    {
        RegisterScript();
        await ExecuteAsync($"""
            INSERT INTO dbo.[{_audit}] (Op, Id, Name) VALUES ('I', 1, 'Alice'), ('U', 2, 'Robert'), ('D', 3, NULL);
            """);

        var read = await ReadAsync(null);
        var rows = await CollectAsync(read.Rows);

        Assert.Equal(
            [ChangeOperation.Insert, ChangeOperation.Update, ChangeOperation.Delete],
            rows.Select(r => r.Operation));
        Assert.Equal(1, rows[0]["Id"]);
        Assert.Equal("Robert", rows[1]["Name"]);
        Assert.Equal("3", read.NewWatermark);
    }

    [Fact]
    public async Task ExcludedColumnsNeverReachTheSchema()
    {
        // Seq is the mechanism's own bookkeeping. Leaving it in would surface a column nobody mapped and
        // that no target has.
        RegisterScript();
        await ExecuteAsync($"INSERT INTO dbo.[{_audit}] (Op, Id, Name) VALUES ('I', 1, 'Alice');");

        var row = Assert.Single(await CollectAsync((await ReadAsync(null)).Rows));

        Assert.Equal(["Op", "Id", "Name"], row.Schema.ColumnNames.Except(["Op"]).Prepend("Op"));
        Assert.DoesNotContain("Seq", row.Schema.ColumnNames);
    }

    [Fact]
    public async Task TheWindowIsBoundedBeforeTheReadSoRowsArrivingMidPassAreNotClaimed()
    {
        // The bounded-window rule ReadResult's own contract states: the watermark is fixed up front, not
        // derived from what was read. A row inserted after the end position is left for the next pass —
        // which is what makes persisting the watermark on success safe.
        RegisterScript();
        await ExecuteAsync($"INSERT INTO dbo.[{_audit}] (Op, Id, Name) VALUES ('I', 1, 'Alice');");

        var read = await ReadAsync(null);
        await ExecuteAsync($"INSERT INTO dbo.[{_audit}] (Op, Id, Name) VALUES ('I', 2, 'Bob');");
        var rows = await CollectAsync(read.Rows);

        Assert.Single(rows);
        Assert.Equal("1", read.NewWatermark);
    }

    [Fact]
    public async Task ReadsOnlyWhatIsNewSinceThePreviousWatermark()
    {
        RegisterScript();
        await ExecuteAsync($"INSERT INTO dbo.[{_audit}] (Op, Id, Name) VALUES ('I', 1, 'Alice');");
        var first = await ReadAsync(null);
        await CollectAsync(first.Rows);

        await ExecuteAsync($"INSERT INTO dbo.[{_audit}] (Op, Id, Name) VALUES ('I', 2, 'Bob');");
        var rows = await CollectAsync((await ReadAsync(first.NewWatermark)).Rows);

        Assert.Equal(2, Assert.Single(rows)["Id"]);
    }

    [Fact]
    public async Task AnUnmappedOperationValueFallsBackRatherThanFailingTheRun()
    {
        // A change feed that grows a fifth operation code should not stop a replication dead. The rows
        // arrive as the default and the operator can extend the map.
        RegisterScript();
        await ExecuteAsync($"INSERT INTO dbo.[{_audit}] (Op, Id, Name) VALUES ('X', 9, 'Odd');");

        Assert.Equal(ChangeOperation.Insert, Assert.Single(await CollectAsync((await ReadAsync(null)).Rows)).Operation);
    }

    [Fact]
    public async Task WithoutTheScriptOption_ItSaysSo()
    {
        var ex = await Assert.ThrowsAsync<ScriptExecutionException>(() =>
            _reader.ReadChangesAsync(
                _connection, Source(), null, ReadIntent.InitialLoad, [], "mapping", [], [],new Dictionary<string, IReadOnlyList<CachedColumn>>(), new Dictionary<string, string>(), CancellationToken.None));

        Assert.Contains(ScriptedQueryReader.ScriptOption, ex.Message);
    }

    [Fact]
    public async Task AnOperationColumnTheQueryDidNotReturn_NamesWhatItDidReturn()
    {
        _configRepository.SaveScript(
            new ScriptDefinition
            {
                Manifest = new ScriptConfig { Name = "wrong-op", Kind = ScriptSlots.SqlColumnExpression, EntryType = "WrongOp" },
                Code = """
                    using DbDataSync.Scripting.Abstractions;

                    public sealed class WrongOp : ISourceQueryBuilder
                    {
                        public SourceQuery? BuildWatermarkQuery(SourceQueryContext c) => null;
                        public SourceQuery BuildReadQuery(SourceQueryContext c) => SourceQuery.Text("SELECT 1 AS Id;");
                        public SourceQueryShape DescribeResult(SourceQueryContext c) => new(OperationColumn: "NotThere");
                    }
                    """,
            },
            new Core.Git.GitAuthor("t", "t@t"));

        var options = new Dictionary<string, string> { [ScriptedQueryReader.ScriptOption] = "wrong-op" };
        var read = await _reader.ReadChangesAsync(
            _connection, Source(), null, ReadIntent.InitialLoad, [], "mapping", [], [],new Dictionary<string, IReadOnlyList<CachedColumn>>(), options, CancellationToken.None);

        var ex = await Assert.ThrowsAsync<ScriptExecutionException>(() => CollectAsync(read.Rows));
        Assert.Contains("NotThere", ex.Message);
        Assert.Contains("Id", ex.Message);
    }

    /// <summary>
    /// Throws if invoked — see the identical fake in <c>TriggerAuditReaderTests</c>. Unlike the other
    /// five consumers, this reader has no "cache empty" throw to test: a query source names no table, so
    /// an empty cache is its normal state, not a missing refresh — see the reasoning in
    /// <see cref="ScriptedQueryReader.ReadChangesAsync"/>. What is worth proving is the same as
    /// everywhere else — that a live catalog is never consulted, populated cache or not.
    /// </summary>
    private sealed class ThrowingTableCatalog : ITableCatalog
    {
        public Task<IReadOnlyList<ColumnMetadata>> GetColumnsAsync(
            System.Data.Common.DbConnection connection, string schema, string table, CancellationToken cancellationToken) =>
            throw new InvalidOperationException(
                "ITableCatalog.GetColumnsAsync was called — phase 91's cache-only reader must never do this.");
    }

    [Fact]
    public async Task ReadChangesAsync_NeverCallsTheLiveCatalog_CacheEmptyOrNot()
    {
        var host = new ScriptHost(
            _configRepository, new ScriptCompiler(new ScriptCacheDirectory(Path.Combine(_repoRoot, "cache2"))));
        var reader = new ScriptedQueryReader(host, MsSqlDialect.Instance, "MsSql", new ThrowingTableCatalog());
        RegisterScript();
        await ExecuteAsync($"INSERT INTO dbo.[{_audit}] (Op, Id, Name) VALUES ('I', 1, 'Alice');");

        var read = await reader.ReadChangesAsync(
            _connection, Source(), null, ReadIntent.InitialLoad, [], "mapping",
            [new CachedColumn("Id", "int", false, true, false)], [],new Dictionary<string, IReadOnlyList<CachedColumn>>(), Options(), CancellationToken.None);
        var rows = await CollectAsync(read.Rows);

        // Reaching here at all is the proof: ThrowingTableCatalog would have failed the test otherwise.
        Assert.Single(rows);
    }
}
