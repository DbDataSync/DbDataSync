using DbDataSync.Core.Config;
using DbDataSync.Drivers.Abstractions;
using DbDataSync.Drivers.Generic;
using Npgsql;
using Xunit;
using DbDataSync.Core.Sql;

namespace DbDataSync.Drivers.Postgres.Tests;

/// <summary>
/// The snapshot and SCD Type 2 writers against a real database, because what they do is decided by
/// SQL and the interesting cases are all about what happens on the *second* pass.
/// </summary>
[Trait("Category", "Integration")]
public sealed class HistorizedWriterTests(PostgresTestDatabase db) : IClassFixture<PostgresTestDatabase>, IAsyncLifetime
{
    private NpgsqlConnection _connection = null!;
    private string _target = null!;
    private string _staging = null!;

    private static readonly List<ColumnMapping> Mappings =
    [
        new() { SourceColumn = "Id", TargetColumn = "Id" },
        new() { SourceColumn = "Name", TargetColumn = "Name" },
    ];

    public async Task InitializeAsync()
    {
        _connection = db.OpenConnection();
        _target = $"hist_{Guid.NewGuid():N}";
        _staging = $"stg_{Guid.NewGuid():N}";

        await ExecuteAsync($"""
            CREATE TABLE public."{_staging}" (
                "Id" INT NOT NULL, "Name" VARCHAR(50) NULL, "__Operation" CHAR(1) NOT NULL);
            """);
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

    private async Task StageAsync(params (int Id, string? Name, string Op)[] rows)
    {
        await ExecuteAsync($"DELETE FROM public.\"{_staging}\";");
        foreach (var (id, name, op) in rows)
        {
            await ExecuteAsync(
                $"INSERT INTO public.\"{_staging}\" VALUES ({id}, " +
                $"{(name is null ? "NULL" : $"'{name}'")}, '{op}');");
        }
    }

    private TableRef Target() => new()
    {
        ConnectionName = "test", Database = db.DatabaseName, Schema = "public", Table = _target,
    };

    private const string MappingName = "historized-writer";

    /// <summary>Matches CreateScd2TargetAsync's own CREATE TABLE — the default, since every test but
    /// the snapshot one below writes into that shape.</summary>
    private static List<CachedColumn> Scd2TargetColumns() =>
    [
        new("DS_VersionKey", "varchar(200)", false, true, false),
        new("Id", "int", false, false, false),
        new("Name", "varchar(50)", true, false, false),
        new("DS_ValidFrom", "timestamp", false, false, false),
        new("DS_ValidTo", "timestamp", true, false, false),
        new("DS_IsCurrent", "boolean", false, false, false),
    ];

    /// <summary>Matches the snapshot test's own CREATE TABLE.</summary>
    private static List<CachedColumn> SnapshotTargetColumns() =>
    [
        new("Id", "int", false, false, false),
        new("Name", "varchar(50)", true, false, false),
        new("DS_SnapshotAt", "timestamp", false, false, false),
    ];

    private Task<WriteResult> ApplyAsync(IChangeWriter writer, IReadOnlyList<CachedColumn>? targetColumns = null) =>
        writer.ApplyAsync(
            _connection, Target(), new StagedChangeSet($"public.\"{_staging}\"", 0), Mappings, MappingName,
            targetColumns ?? Scd2TargetColumns(),
            // The business key, which cannot be inferred from an SCD2 target whose primary key is the
            // generated surrogate — see Scd2Writer.NaturalKeyOption.
            new Dictionary<string, string> { [Scd2Writer.NaturalKeyOption] = "Id" },
            CancellationToken.None);

    private async Task<List<(int Id, string? Name, bool Current, bool Closed)>> VersionsAsync()
    {
        await using var cmd = _connection.CreateCommand();
        cmd.CommandText =
            $"SELECT \"Id\", \"Name\", \"DS_IsCurrent\", \"DS_ValidTo\" IS NOT NULL " +
            $"FROM public.\"{_target}\" ORDER BY \"DS_ValidFrom\", \"Id\";";
        await using var reader = await cmd.ExecuteReaderAsync();
        var rows = new List<(int, string?, bool, bool)>();
        while (await reader.ReadAsync())
            rows.Add((reader.GetInt32(0), reader.IsDBNull(1) ? null : reader.GetString(1),
                reader.GetBoolean(2), reader.GetBoolean(3)));
        return rows;
    }

    private async Task CreateScd2TargetAsync() => await ExecuteAsync($"""
        CREATE TABLE public."{_target}" (
            "DS_VersionKey" VARCHAR(200) NOT NULL PRIMARY KEY,
            "Id" INT NOT NULL,
            "Name" VARCHAR(50) NULL,
            "DS_ValidFrom" TIMESTAMP NOT NULL,
            "DS_ValidTo" TIMESTAMP NULL,
            "DS_IsCurrent" BOOLEAN NOT NULL);
        """);

    /// <summary>
    /// **No dedup, ever.** Two identical passes produce two complete copies — a snapshot that skipped
    /// unchanged rows would produce copies that are not snapshots, where a missing row means
    /// "unchanged" to somebody who knows the implementation and "deleted" to everybody else.
    /// </summary>
    [Fact]
    public async Task ASnapshotRunTwiceAgainstAnUnchangedSource_WritesTwoCompleteCopies()
    {
        await ExecuteAsync($"""
            CREATE TABLE public."{_target}" (
                "Id" INT NOT NULL, "Name" VARCHAR(50) NULL, "DS_SnapshotAt" TIMESTAMP NOT NULL);
            """);
        var writer = new SnapshotWriter(PostgresDialect.Instance, PostgresCatalog.Instance);

        await StageAsync((1, "a", "I"), (2, "b", "I"));
        await ApplyAsync(writer, SnapshotTargetColumns());
        await ApplyAsync(writer, SnapshotTargetColumns());

        await using var cmd = _connection.CreateCommand();
        cmd.CommandText =
            $"SELECT COUNT(*), COUNT(DISTINCT \"DS_SnapshotAt\") FROM public.\"{_target}\";";
        await using var reader = await cmd.ExecuteReaderAsync();
        await reader.ReadAsync();

        Assert.Equal(4, reader.GetInt64(0));
        // Two markers, so "the most recent snapshot" is a filter rather than a guess — which is what
        // phase 54 reads.
        Assert.Equal(2, reader.GetInt64(1));
    }

    [Fact]
    public async Task ANewKey_OpensOneVersion()
    {
        await CreateScd2TargetAsync();
        await StageAsync((1, "a", "I"));

        var written = await ApplyAsync(new Scd2Writer(PostgresDialect.Instance, PostgresCatalog.Instance));

        Assert.Equal(1, written.RowsWritten);
        var version = Assert.Single(await VersionsAsync());
        Assert.Equal((1, "a", true, false), version);
    }

    [Fact]
    public async Task AChangedKey_ClosesTheOldVersionAndOpensANewOne()
    {
        await CreateScd2TargetAsync();
        var writer = new Scd2Writer(PostgresDialect.Instance, PostgresCatalog.Instance);

        await StageAsync((1, "a", "I"));
        await ApplyAsync(writer);

        await StageAsync((1, "b", "U"));
        await ApplyAsync(writer);

        var versions = await VersionsAsync();
        Assert.Equal(2, versions.Count);
        Assert.Equal((1, "a", false, true), versions[0]);
        Assert.Equal((1, "b", true, false), versions[1]);
    }

    /// <summary>The whole economy of SCD2: a pass over unchanged data writes nothing at all.</summary>
    [Fact]
    public async Task AnUnchangedKey_ProducesNoWriteAtAll()
    {
        await CreateScd2TargetAsync();
        var writer = new Scd2Writer(PostgresDialect.Instance, PostgresCatalog.Instance);

        await StageAsync((1, "a", "I"));
        await ApplyAsync(writer);

        await StageAsync((1, "a", "U"));
        var second = await ApplyAsync(writer);

        Assert.Equal(0, second.RowsWritten);
        Assert.Single(await VersionsAsync());
    }

    /// <summary>
    /// The null-safety the statement test asserts, proved against a database that really does treat
    /// `a <> NULL` as unknown. Without it the version never closes and the target reports stale data
    /// as current — silently.
    /// </summary>
    [Fact]
    public async Task AValueBecomingNull_ClosesTheVersion()
    {
        await CreateScd2TargetAsync();
        var writer = new Scd2Writer(PostgresDialect.Instance, PostgresCatalog.Instance);

        await StageAsync((1, "a", "I"));
        await ApplyAsync(writer);

        await StageAsync((1, null, "U"));
        await ApplyAsync(writer);

        var versions = await VersionsAsync();
        Assert.Equal(2, versions.Count);
        Assert.Equal((1, "a", false, true), versions[0]);
        Assert.Equal((1, null, true, false), versions[1]);
    }

    [Fact]
    public async Task AValueArrivingWhereThereWasNone_ClosesTheVersion()
    {
        await CreateScd2TargetAsync();
        var writer = new Scd2Writer(PostgresDialect.Instance, PostgresCatalog.Instance);

        await StageAsync((1, null, "I"));
        await ApplyAsync(writer);

        await StageAsync((1, "a", "U"));
        await ApplyAsync(writer);

        Assert.Equal(2, (await VersionsAsync()).Count);
    }

    /// <summary>With a reader that reports deletes, the version closes and nothing replaces it — the
    /// difference between "this record ended" and "this record changed".</summary>
    [Fact]
    public async Task ADeletedKey_ClosesItsVersionWithNoReplacement()
    {
        await CreateScd2TargetAsync();
        var writer = new Scd2Writer(PostgresDialect.Instance, PostgresCatalog.Instance);

        await StageAsync((1, "a", "I"));
        await ApplyAsync(writer);

        await StageAsync((1, "a", "D"));
        await ApplyAsync(writer);

        var version = Assert.Single(await VersionsAsync());
        Assert.Equal((1, "a", false, true), version);
    }

    /// <summary>
    /// The accepted limitation, asserted so it behaves as documented rather than being silently wrong:
    /// paired with a delete-blind reader, a key that disappears at the source stays current forever.
    /// A delete-blind reader simply never stages a 'D'.
    /// </summary>
    [Fact]
    public async Task WithADeleteBlindReader_ADisappearedKeyStaysCurrent()
    {
        await CreateScd2TargetAsync();
        var writer = new Scd2Writer(PostgresDialect.Instance, PostgresCatalog.Instance);

        await StageAsync((1, "a", "I"), (2, "b", "I"));
        await ApplyAsync(writer);

        // Row 2 is gone at the source, and a reader that cannot see deletes stages only what remains.
        await StageAsync((1, "a", "I"));
        await ApplyAsync(writer);

        var versions = await VersionsAsync();
        Assert.Equal(2, versions.Count);
        Assert.All(versions, v => Assert.True(v.Current));
    }
}
