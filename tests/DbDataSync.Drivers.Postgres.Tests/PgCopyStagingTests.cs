using DbDataSync.Core.Config;
using DbDataSync.Core.Sql;
using DbDataSync.Drivers.Abstractions;
using DbDataSync.Drivers.Generic;
using Npgsql;
using Xunit;

namespace DbDataSync.Drivers.Postgres.Tests;

/// <summary>
/// Phase 38's binary <c>COPY</c> staging against a real server, and — in the same test body, run twice —
/// the batched-<c>INSERT</c> provider it is an alternative to.
/// <para>
/// One body, two Kinds, is the whole design of this file. The claim phase 38 makes is not "COPY works"
/// but "COPY is interchangeable with the generic provider": same staging table, same DDL, same columns,
/// so every reader and writer either side of it is unaffected. A test that only exercised the new
/// provider could pass while the two quietly disagreed about, say, whether a <c>numeric</c> comes back
/// with its scale.
/// </para>
/// <para>
/// The one place they are *not* interchangeable is the last test here, and it is deliberate: binary
/// <c>COPY</c> sends a value in the column's own wire format and the server converts nothing, where a
/// parameterised <c>INSERT</c> lets it convert. That is the reason both stay registered, and it is
/// worth a test showing the difference rather than a sentence asserting it.
/// </para>
/// </summary>
[Trait("Category", "Integration")]
public sealed class PgCopyStagingTests(PostgresTestDatabase db) : IClassFixture<PostgresTestDatabase>, IAsyncLifetime
{
    private readonly BatchReloadReader _reader = new(PostgresDialect.Instance, PostgresCatalog.Instance, PostgresValueBinding.Instance);
    private readonly DeleteInsertWriter _writer = new(PostgresDialect.Instance, PostgresCatalog.Instance, PostgresValueBinding.Instance);

    private NpgsqlConnection _source = null!;
    private NpgsqlConnection _target = null!;
    private string _sourceTable = null!;
    private string _targetTable = null!;

    private const string MappingName = "pg-copy-staging";

    /// <summary>
    /// Deliberately wider than the pipeline tests' four columns: the types phase 38 named as the ones
    /// to cover, because each is written through a different Npgsql converter and a wrong
    /// <c>NpgsqlDbType</c> for any one of them is a defect that only shows up on the wire.
    /// </summary>
    private const string Columns = """
        id integer primary key,
        name text not null,
        amount numeric(18,2),
        ratio double precision,
        small smallint,
        big bigint,
        flag boolean,
        code character varying(10),
        ident uuid,
        payload bytea,
        when_date date,
        modified_at timestamp
        """;

    private static readonly string[] ColumnNames =
        ["id", "name", "amount", "ratio", "small", "big", "flag", "code", "ident", "payload", "when_date", "modified_at"];

    private static readonly List<ColumnMapping> Mappings =
        [.. ColumnNames.Select(c => new ColumnMapping { SourceColumn = c, TargetColumn = c })];

    private static List<CachedColumn> CachedColumns() =>
    [
        new("id", "integer", false, true, false),
        new("name", "text", false, false, false),
        new("amount", "numeric(18,2)", true, false, false),
        new("ratio", "double precision", true, false, false),
        new("small", "smallint", true, false, false),
        new("big", "bigint", true, false, false),
        new("flag", "boolean", true, false, false),
        new("code", "character varying(10)", true, false, false),
        new("ident", "uuid", true, false, false),
        new("payload", "bytea", true, false, false),
        new("when_date", "date", true, false, false),
        new("modified_at", "timestamp", true, false, false),
    ];

    public async Task InitializeAsync()
    {
        _source = db.OpenConnection();
        _target = db.OpenConnection();
        var suffix = Guid.NewGuid().ToString("N");
        _sourceTable = $"copysrc_{suffix}";
        _targetTable = $"copytgt_{suffix}";

        await ExecuteAsync(_source, $"CREATE TABLE public.\"{_sourceTable}\" ({Columns});");
        await ExecuteAsync(_target, $"CREATE TABLE public.\"{_targetTable}\" ({Columns});");
    }

    public async Task DisposeAsync()
    {
        await _source.DisposeAsync();
        await _target.DisposeAsync();
    }

    private static async Task ExecuteAsync(NpgsqlConnection connection, string sql)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>Both Kinds the Postgres driver registers, so the theory below cannot silently drop one.</summary>
    public static TheoryData<string> StagingKinds()
    {
        var data = new TheoryData<string>();
        foreach (var provider in new PostgresDriver().StagingProviders)
            data.Add(provider.Kind);
        return data;
    }

    private static IStagingProvider Staging(string kind) =>
        new PostgresDriver().StagingProviders.Single(p => p.Kind == kind);

    private SourceTableRef Source() =>
        new() { ConnectionName = "src", Database = db.DatabaseName, Schema = "public", Table = _sourceTable };

    private TableRef Target() =>
        new() { ConnectionName = "tgt", Database = db.DatabaseName, Schema = "public", Table = _targetTable };

    private async Task<long> ReloadAsync(string stagingKind)
    {
        var options = new Dictionary<string, string>();
        var staging = Staging(stagingKind);
        var read = await _reader.ReadChangesAsync(
            _source, Source(), null, ReadIntent.InitialLoad, Mappings, MappingName, CachedColumns(), options, CancellationToken.None);
        var staged = await staging.StageAsync(
            _target, Target(), read.Rows, Mappings, MappingName, CachedColumns(), options, CancellationToken.None);
        try
        {
            return (await _writer.ApplyAsync(
                _target, Target(), staged, Mappings, MappingName, CachedColumns(), options, CancellationToken.None)).RowsWritten;
        }
        finally
        {
            await staging.CleanupAsync(_target, staged, CancellationToken.None);
        }
    }

    /// <summary>Every column of every row, as the server hands it back — compared between the two
    /// tables rather than against literals, so a type that round-trips differently shows up as a
    /// difference rather than as a test that was written to match whichever it did.</summary>
    private static async Task<List<object?[]>> ReadAllAsync(NpgsqlConnection connection, string table)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = $"SELECT {string.Join(", ", ColumnNames.Select(c => $"\"{c}\""))} " +
                          $"FROM public.\"{table}\" ORDER BY id;";
        var rows = new List<object?[]>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var values = new object?[ColumnNames.Length];
            for (var i = 0; i < values.Length; i++)
                values[i] = reader.IsDBNull(i) ? null : reader.GetValue(i);
            rows.Add(values);
        }
        return rows;
    }

    private async Task InsertSourceRowsAsync() => await ExecuteAsync(_source, $"""
        INSERT INTO public."{_sourceTable}" VALUES
            (1, 'Alice', 10.50, 1.25, 7, 9000000000, true, 'AB-1',
             '11111111-2222-3333-4444-555555555555', '\xdeadbeef', DATE '2026-01-31',
             TIMESTAMP '2026-01-01 09:00:00.123456'),
            (2, 'Bob', -0.01, -1.5e100, -32768, -9000000000, false, '',
             '00000000-0000-0000-0000-000000000000', '\x', DATE '1900-01-01',
             TIMESTAMP '1999-12-31 23:59:59.999999');
        """);

    /// <summary>
    /// The interchangeability claim, for the types phase 38 named. Both providers stage the same rows
    /// into the same table shape, and the writer downstream of them produces a target that matches the
    /// source column for column.
    /// </summary>
    [Theory]
    [MemberData(nameof(StagingKinds))]
    public async Task EitherStagingProvider_LandsEveryColumnAsTheSourceHadIt(string stagingKind)
    {
        await InsertSourceRowsAsync();

        Assert.Equal(2, await ReloadAsync(stagingKind));
        Assert.Equal(await ReadAllAsync(_source, _sourceTable), await ReadAllAsync(_target, _targetTable));
    }

    /// <summary>
    /// A null is a different thing on the wire from a value, and binary <c>COPY</c> writes it with a
    /// length prefix of -1 rather than by omitting the cell — get that wrong and every column after it
    /// in the row shifts, which is the failure mode this exists to catch.
    /// </summary>
    [Theory]
    [MemberData(nameof(StagingKinds))]
    public async Task EitherStagingProvider_LandsNullsAsNulls(string stagingKind)
    {
        await ExecuteAsync(_source, $"""
            INSERT INTO public."{_sourceTable}" (id, name) VALUES (1, 'only the key and the name');
            """);

        Assert.Equal(1, await ReloadAsync(stagingKind));

        var target = Assert.Single(await ReadAllAsync(_target, _targetTable));
        Assert.Equal(1, target[0]);
        Assert.Equal("only the key and the name", target[1]);
        Assert.All(target[2..], v => Assert.Null(v));
    }

    /// <summary>A delete's marker reaches the target through the same stream as a value would, and it
    /// is the one cell staging supplies rather than the reader.</summary>
    [Theory]
    [MemberData(nameof(StagingKinds))]
    public async Task EitherStagingProvider_ReloadRemovesRowsDeletedAtTheSource(string stagingKind)
    {
        await InsertSourceRowsAsync();
        await ReloadAsync(stagingKind);

        await ExecuteAsync(_source, $"DELETE FROM public.\"{_sourceTable}\" WHERE id = 2;");
        await ReloadAsync(stagingKind);

        var remaining = Assert.Single(await ReadAllAsync(_target, _targetTable));
        Assert.Equal(1, remaining[0]);
    }

    /// <summary>A pass with no rows at all still opens and completes the stream, leaving the staging
    /// table created and empty rather than half-open on the connection.</summary>
    [Theory]
    [MemberData(nameof(StagingKinds))]
    public async Task EitherStagingProvider_StagesAnEmptyBatchWithoutFailing(string stagingKind)
    {
        Assert.Equal(0, await ReloadAsync(stagingKind));
        Assert.Empty(await ReadAllAsync(_target, _targetTable));
    }

    /// <summary>
    /// The difference that keeps both providers registered, shown rather than asserted.
    /// <para>
    /// A <c>timestamp without time zone</c> at the source mapped to a <c>timestamp with time zone</c>
    /// at the target: Npgsql reads the first as a <see cref="DateTime"/> with
    /// <see cref="DateTimeKind.Unspecified"/> and will not write one of those into the second, because
    /// choosing a zone for it would be inventing information. Through a parameterised <c>INSERT</c> the
    /// *server* converts it, using its own TimeZone setting — which works, and is exactly the sort of
    /// silent assumption binary <c>COPY</c> cannot make.
    /// </para>
    /// <para>
    /// So COPY fails, and the point of the test is that it fails *legibly*: naming the column, saying
    /// why, and naming the Kind that can carry it — which is then shown to actually carry it.
    /// </para>
    /// </summary>
    [Fact]
    public async Task AValueBinaryCopyCannotConvert_FailsNamingTheColumnAndTheAlternative()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var source = $"tzsrc_{suffix}";
        var target = $"tztgt_{suffix}";
        await ExecuteAsync(_source, $"CREATE TABLE public.\"{source}\" (id integer primary key, at timestamp);");
        await ExecuteAsync(_target, $"CREATE TABLE public.\"{target}\" (id integer primary key, at timestamptz);");
        await ExecuteAsync(_source, $"INSERT INTO public.\"{source}\" VALUES (1, TIMESTAMP '2026-01-01 09:00:00');");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => StageTimestampTzAsync(
            Staging(PostgresDriverKinds.CopyStaging), source, target));

        Assert.Contains("'at'", ex.Message);
        Assert.Contains("Kind=Unspecified", ex.Message);
        Assert.Contains(PostgresDriverKinds.StagingTable, ex.Message);

        // And the alternative it names really is one: the same rows, through the same mapping, staged.
        var generic = Staging(PostgresDriverKinds.StagingTable);
        var staged = await StageTimestampTzAsync(generic, source, target);
        try
        {
            Assert.Equal(1, staged.RowCount);
        }
        finally
        {
            await generic.CleanupAsync(_target, staged, CancellationToken.None);
        }
    }

    /// <summary>
    /// A failed copy leaves nothing behind. A staging table is a real table, so a pass that dies
    /// part-way through the stream has to drop it itself — the caller is only handed a change set to
    /// clean up when staging *succeeded*, so anything left here would accumulate in the target's own
    /// schema, one table per failed pass, until somebody noticed.
    /// </summary>
    [Fact]
    public async Task AFailedCopy_LeavesNoStagingTableBehind()
    {
        var before = await StagingTableCountAsync();

        var suffix = Guid.NewGuid().ToString("N");
        var source = $"tzsrc_{suffix}";
        var target = $"tztgt_{suffix}";
        await ExecuteAsync(_source, $"CREATE TABLE public.\"{source}\" (id integer primary key, at timestamp);");
        await ExecuteAsync(_target, $"CREATE TABLE public.\"{target}\" (id integer primary key, at timestamptz);");
        await ExecuteAsync(_source, $"INSERT INTO public.\"{source}\" VALUES (1, TIMESTAMP '2026-01-01 09:00:00');");

        await Assert.ThrowsAsync<InvalidOperationException>(() => StageTimestampTzAsync(
            Staging(PostgresDriverKinds.CopyStaging), source, target));

        Assert.Equal(before, await StagingTableCountAsync());
    }

    /// <summary>
    /// Reads a <c>timestamp</c> source column and stages it against a <c>timestamptz</c> target one —
    /// the mapping the two staging providers answer differently, shared by the two tests that care.
    /// </summary>
    private async Task<StagedChangeSet> StageTimestampTzAsync(IStagingProvider staging, string source, string target)
    {
        List<ColumnMapping> mappings =
            [new() { SourceColumn = "id", TargetColumn = "id" }, new() { SourceColumn = "at", TargetColumn = "at" }];
        List<CachedColumn> cached =
            [new("id", "integer", false, true, false), new("at", "timestamp with time zone", true, false, false)];
        var sourceRef = new SourceTableRef
        {
            ConnectionName = "src", Database = db.DatabaseName, Schema = "public", Table = source,
        };
        var targetRef = new TableRef
        {
            ConnectionName = "tgt", Database = db.DatabaseName, Schema = "public", Table = target,
        };
        var options = new Dictionary<string, string>();

        var read = await _reader.ReadChangesAsync(
            _source, sourceRef, null, ReadIntent.InitialLoad, mappings, MappingName, cached, options, CancellationToken.None);
        return await staging.StageAsync(
            _target, targetRef, read.Rows, mappings, MappingName, cached, options, CancellationToken.None);
    }

    /// <summary>
    /// How many staging tables exist right now. Safe as a plain count because xunit runs a class's
    /// tests one at a time and this class has a database of its own — the only thing creating
    /// <c>DS_STG_</c> tables in it is the test currently running.
    /// </summary>
    private async Task<long> StagingTableCountAsync()
    {
        await using var cmd = _target.CreateCommand();
        cmd.CommandText =
            "SELECT count(*) FROM pg_tables WHERE schemaname = 'public' AND tablename LIKE 'DS\\_STG\\_%';";
        return (long)(await cmd.ExecuteScalarAsync())!;
    }
}
