using DbDataSync.Core.Config;
using DbDataSync.Core.Sql;
using DbDataSync.Drivers.Abstractions;
using DbDataSync.Drivers.Generic;
using Microsoft.Data.SqlClient;
using Xunit;

namespace DbDataSync.Drivers.MsSql.Tests;

/// <summary>
/// The batch-reload driver pieces against a real SQL Server: the segment-scoped reader, and the two
/// reconciling writers. The properties under test here can't be checked without an engine — that a
/// CTE-scoped MERGE really does leave out-of-segment rows alone, that delete+insert really is atomic,
/// and that MERGE against a CTE is even legal T-SQL.
/// <para>
/// **Phase 191S**: the reader under test is the generic <see cref="BatchReloadReader"/>, not a
/// dedicated MsSql class — <c>MsSqlBatchReloadReader</c> was retired as a leftover from before the
/// generic pipeline existed (it duplicated the same SQL this class already builds, plus a live
/// catalog call phase 91's cache-only migration never reached). <c>MsSqlDriver</c> now registers the
/// generic reader under both the generic and the MsSql-specific Kind string.
/// </para>
/// </summary>
[Trait("Category", "Integration")]
public sealed class MsSqlBatchReloadTests(MsSqlTestDatabase db) : IClassFixture<MsSqlTestDatabase>, IAsyncLifetime
{
    private readonly BatchReloadReader _reader = new(MsSqlDialect.Instance, MsSqlValueBinding.Instance);
    private readonly MsSqlStagingTableProvider _staging = new();
    private SqlConnection _sourceConnection = null!;
    private SqlConnection _targetConnection = null!;
    private string _sourceTable = null!;
    private string _targetTable = null!;

    private static readonly List<ColumnMapping> Mappings =
    [
        new() { SourceColumn = "Id", TargetColumn = "Id" },
        new() { SourceColumn = "Region", TargetColumn = "Region" },
        new() { SourceColumn = "Name", TargetColumn = "Name" },
    ];

    public async Task InitializeAsync()
    {
        _sourceConnection = db.OpenConnection();
        _targetConnection = db.OpenConnection();
        var suffix = Guid.NewGuid().ToString("N");
        _sourceTable = $"ReloadSrc_{suffix}";
        _targetTable = $"ReloadTgt_{suffix}";

        await ExecuteAsync(_sourceConnection, $"""
            CREATE TABLE dbo.[{_sourceTable}] (
                Id INT NOT NULL PRIMARY KEY, Region NVARCHAR(20) NOT NULL, Name NVARCHAR(50) NOT NULL);
            """);
        await ExecuteAsync(_targetConnection, $"""
            CREATE TABLE dbo.[{_targetTable}] (
                Id INT NOT NULL PRIMARY KEY, Region NVARCHAR(20) NOT NULL, Name NVARCHAR(50) NOT NULL);
            """);
    }

    public Task DisposeAsync()
    {
        _sourceConnection.Dispose();
        _targetConnection.Dispose();
        return Task.CompletedTask;
    }

    private static async Task ExecuteAsync(SqlConnection connection, string sql)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }

    private SourceTableRef Source(string? table = null) =>
        new() { ConnectionName = "src", Database = db.DatabaseName, Schema = "dbo", Table = table ?? _sourceTable };

    private TableRef Target(string? table = null) =>
        new() { ConnectionName = "tgt", Database = db.DatabaseName, Schema = "dbo", Table = table ?? _targetTable };

    private static Dictionary<string, string> SegmentOptions(BatchReloadSegment? segment) =>
        segment is null
            ? []
            : new Dictionary<string, string> { [SegmentSerializer.SegmentOptionKey] = SegmentSerializer.Serialize(segment) };

    private const string MappingName = "mssql-batch-reload";

    /// <summary>The standard target's shape — Id/Region/Name, Id the primary key — matching the
    /// fixture's own CREATE TABLE. Callers against a keyless or identity target pass their own.</summary>
    private static List<CachedColumn> TargetColumns(bool identity = false) =>
    [
        new("Id", "int", false, true, identity),
        new("Region", "nvarchar(20)", false, false, false),
        new("Name", "nvarchar(50)", false, false, false),
    ];

    private static List<CachedColumn> KeylessTargetColumns() =>
    [
        new("Id", "int", false, false, false),
        new("Region", "nvarchar(20)", false, false, false),
        new("Name", "nvarchar(50)", false, false, false),
    ];

    /// <summary>One reload of one segment, end to end: read the segment, stage it, apply it — the same
    /// sequence RunExecutor performs per work item.</summary>
    private async Task<long> ReloadAsync(
        IChangeWriter writer,
        BatchReloadSegment? segment,
        IReadOnlyList<ColumnMapping>? mappings = null,
        string? sourceTable = null,
        string? targetTable = null,
        IReadOnlyList<CachedColumn>? targetColumns = null)
    {
        var options = SegmentOptions(segment);
        var columnMappings = mappings ?? Mappings;

        var read = await _reader.ReadChangesAsync(
            _sourceConnection, Source(sourceTable), previousWatermark: null, ReadIntent.InitialLoad, [], MappingName, [], [], options, CancellationToken.None);
        var staged = await _staging.StageAsync(
            _targetConnection, Target(targetTable), read.Rows, columnMappings, MappingName, [], new Dictionary<string, string>(),
            CancellationToken.None);
        var written = await writer.ApplyAsync(
            _targetConnection, Target(targetTable), staged, columnMappings, MappingName, targetColumns ?? TargetColumns(), options,
            CancellationToken.None);

        return written.RowsWritten;
    }

    private async Task<Dictionary<int, (string Region, string Name)>> GetTargetRowsAsync(string? table = null)
    {
        await using var cmd = _targetConnection.CreateCommand();
        cmd.CommandText = $"SELECT Id, Region, Name FROM dbo.[{table ?? _targetTable}];";
        var results = new Dictionary<int, (string, string)>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            results[reader.GetInt32(0)] = (reader.GetString(1), reader.GetString(2));
        return results;
    }

    // ---- MsSqlMergeReconcileWriter ----------------------------------------------------------

    /// <summary>
    /// The central guarantee of a segmented reload: within the segment the target ends up matching the
    /// source exactly — including removing rows the source no longer has — and outside it nothing is
    /// touched at all, not even a row that's equally missing from the source.
    /// </summary>
    [Fact]
    public async Task MergeReconcile_MakesTheSegmentMatchTheSource_AndLeavesEverythingElseAlone()
    {
        await ExecuteAsync(_sourceConnection, $"""
            INSERT INTO dbo.[{_sourceTable}] (Id, Region, Name) VALUES
                (1, 'EU', 'Alice'), (2, 'EU', 'Bob-updated'), (5, 'US', 'Eve');
            """);
        await ExecuteAsync(_targetConnection, $"""
            INSERT INTO dbo.[{_targetTable}] (Id, Region, Name) VALUES
                (2, 'EU', 'Bob-stale'), (3, 'EU', 'Carol-deleted-at-source'),
                (5, 'US', 'Eve'), (6, 'US', 'Frank-deleted-at-source');
            """);

        await ReloadAsync(new MsSqlMergeReconcileWriter(), new ListSegment("Region", ["EU"]));

        var rows = await GetTargetRowsAsync();
        Assert.Equal("Alice", rows[1].Name);          // inserted: in the source, missing from the target
        Assert.Equal("Bob-updated", rows[2].Name);    // updated: differed
        Assert.False(rows.ContainsKey(3));            // deleted: in the segment, gone from the source
        Assert.Equal("Eve", rows[5].Name);            // untouched: outside the segment
        Assert.Equal("Frank-deleted-at-source", rows[6].Name); // untouched despite being gone at source
    }

    [Fact]
    public async Task MergeReconcile_WithARangeSegment_ScopesByTheRangeBounds()
    {
        await ExecuteAsync(_sourceConnection, $"""
            INSERT INTO dbo.[{_sourceTable}] (Id, Region, Name) VALUES (1, 'EU', 'One'), (7, 'EU', 'Seven');
            """);
        await ExecuteAsync(_targetConnection, $"""
            INSERT INTO dbo.[{_targetTable}] (Id, Region, Name) VALUES (3, 'EU', 'Three'), (7, 'EU', 'Seven-stale');
            """);

        // Half-open [1, 5): Id 3 is inside and absent from the source, Id 7 is outside.
        await ReloadAsync(new MsSqlMergeReconcileWriter(), new RangeSegment("Id", "1", "5"));

        var rows = await GetTargetRowsAsync();
        Assert.Equal("One", rows[1].Name);
        Assert.False(rows.ContainsKey(3));
        Assert.Equal("Seven-stale", rows[7].Name);
    }

    [Fact]
    public async Task MergeReconcile_WithNoSegment_ReconcilesTheWholeTable()
    {
        await ExecuteAsync(_sourceConnection, $"INSERT INTO dbo.[{_sourceTable}] (Id, Region, Name) VALUES (1, 'EU', 'One');");
        await ExecuteAsync(_targetConnection, $"""
            INSERT INTO dbo.[{_targetTable}] (Id, Region, Name) VALUES (2, 'EU', 'Two'), (9, 'US', 'Nine');
            """);

        await ReloadAsync(new MsSqlMergeReconcileWriter(), segment: null);

        var rows = await GetTargetRowsAsync();
        Assert.Equal(["One"], rows.Values.Select(v => v.Name));
    }

    /// <summary>
    /// The existing upsert-only writer, reused unchanged on a reload, deliberately does *not* remove
    /// rows the source no longer has. Pinned as a test so that choosing it for a mapping reads as an
    /// intentional trade-off rather than as a bug someone later "fixes".
    /// </summary>
    [Fact]
    public async Task MergeWriter_OnAReload_LeavesRowsMissingFromTheSourceInPlace()
    {
        await ExecuteAsync(_sourceConnection, $"INSERT INTO dbo.[{_sourceTable}] (Id, Region, Name) VALUES (1, 'EU', 'One');");
        await ExecuteAsync(_targetConnection, $"""
            INSERT INTO dbo.[{_targetTable}] (Id, Region, Name) VALUES (3, 'EU', 'Carol-deleted-at-source');
            """);

        await ReloadAsync(new MsSqlMergeWriter(), new ListSegment("Region", ["EU"]));

        var rows = await GetTargetRowsAsync();
        Assert.Equal("One", rows[1].Name);
        Assert.True(rows.ContainsKey(3));
        Assert.False(new MsSqlMergeWriter().SupportsReconciliation);
    }

    // ---- MsSqlDeleteInsertWriter -------------------------------------------------------------

    [Fact]
    public async Task DeleteInsert_ReplacesTheSegmentAndLeavesEverythingElseAlone()
    {
        await ExecuteAsync(_sourceConnection, $"""
            INSERT INTO dbo.[{_sourceTable}] (Id, Region, Name) VALUES (1, 'EU', 'Alice'), (2, 'EU', 'Bob');
            """);
        await ExecuteAsync(_targetConnection, $"""
            INSERT INTO dbo.[{_targetTable}] (Id, Region, Name) VALUES
                (2, 'EU', 'Bob-stale'), (3, 'EU', 'Carol-deleted-at-source'), (9, 'US', 'Nine');
            """);

        var written = await ReloadAsync(new MsSqlDeleteInsertWriter(), new ListSegment("Region", ["EU"]));

        Assert.Equal(2, written);
        var rows = await GetTargetRowsAsync();
        Assert.Equal("Alice", rows[1].Name);
        Assert.Equal("Bob", rows[2].Name);
        Assert.False(rows.ContainsKey(3));
        Assert.Equal("Nine", rows[9].Name);
    }

    /// <summary>
    /// The real differentiator from both MERGE-based writers: nothing is joined row by row, so the
    /// target needs no primary key at all.
    /// </summary>
    [Fact]
    public async Task DeleteInsert_WorksAgainstATargetWithNoPrimaryKey()
    {
        var keyless = $"ReloadKeyless_{Guid.NewGuid():N}";
        await ExecuteAsync(_targetConnection, $"""
            CREATE TABLE dbo.[{keyless}] (Id INT NOT NULL, Region NVARCHAR(20) NOT NULL, Name NVARCHAR(50) NOT NULL);
            """);
        await ExecuteAsync(_sourceConnection, $"INSERT INTO dbo.[{_sourceTable}] (Id, Region, Name) VALUES (1, 'EU', 'Alice');");
        await ExecuteAsync(_targetConnection, $"INSERT INTO dbo.[{keyless}] (Id, Region, Name) VALUES (99, 'EU', 'Stale');");

        await ReloadAsync(
            new MsSqlDeleteInsertWriter(), new ListSegment("Region", ["EU"]), targetTable: keyless,
            targetColumns: KeylessTargetColumns());

        var rows = await GetTargetRowsAsync(keyless);
        Assert.Equal(["Alice"], rows.Values.Select(v => v.Name));

        // The same reload through a MERGE writer is rejected up front rather than half-applied.
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => ReloadAsync(
                new MsSqlMergeReconcileWriter(), new ListSegment("Region", ["EU"]), targetTable: keyless,
                targetColumns: KeylessTargetColumns()));
    }

    /// <summary>
    /// Delete and insert share one transaction, so the window where the segment is empty is never
    /// observable: a concurrent reader either waits and sees the new contents, or sees the old ones.
    /// A reader that saw zero rows would mean the transaction wasn't doing its job.
    /// </summary>
    [Fact]
    public async Task DeleteInsert_IsAtomic_SoNoConcurrentReaderEverSeesTheSegmentEmpty()
    {
        await ExecuteAsync(_sourceConnection, $"""
            INSERT INTO dbo.[{_sourceTable}] (Id, Region, Name)
            SELECT TOP (2000) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)), 'EU', 'Row'
            FROM sys.all_objects a CROSS JOIN sys.all_objects b;
            """);
        await ExecuteAsync(_targetConnection, $"""
            INSERT INTO dbo.[{_targetTable}] (Id, Region, Name)
            SELECT TOP (2000) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)), 'EU', 'Stale'
            FROM sys.all_objects a CROSS JOIN sys.all_objects b;
            """);

        var observed = new List<int>();
        var reload = ReloadAsync(new MsSqlDeleteInsertWriter(), new ListSegment("Region", ["EU"]));

        await using (var observer = db.OpenConnection())
        {
            while (!reload.IsCompleted)
            {
                await using var cmd = observer.CreateCommand();
                cmd.CommandText = $"SELECT COUNT(*) FROM dbo.[{_targetTable}] WHERE Region = 'EU';";
                observed.Add((int)(await cmd.ExecuteScalarAsync())!);
            }
        }

        await reload;
        Assert.NotEmpty(observed);
        Assert.DoesNotContain(0, observed);
        Assert.Equal(2000, (await GetTargetRowsAsync()).Count);
    }

    // ---- Identity targets --------------------------------------------------------------------

    /// <summary>
    /// Reloading into a target whose key is an IDENTITY column means inserting explicit key values,
    /// which SQL Server rejects unless IDENTITY_INSERT is on. Detected from the catalog, so a mapping
    /// against an identity target doesn't need the operator to know about it.
    /// </summary>
    [Theory]
    [InlineData("reconcile")]
    [InlineData("delete-insert")]
    public async Task ReloadWriters_InsertExplicitKeysIntoAnIdentityTarget(string writerName)
    {
        var identityTable = $"ReloadIdentity_{Guid.NewGuid():N}";
        await ExecuteAsync(_targetConnection, $"""
            CREATE TABLE dbo.[{identityTable}] (
                Id INT IDENTITY(1,1) NOT NULL PRIMARY KEY, Region NVARCHAR(20) NOT NULL, Name NVARCHAR(50) NOT NULL);
            """);
        await ExecuteAsync(_sourceConnection, $"""
            INSERT INTO dbo.[{_sourceTable}] (Id, Region, Name) VALUES (41, 'EU', 'Alice'), (42, 'EU', 'Bob');
            """);

        IChangeWriter writer = writerName == "reconcile"
            ? new MsSqlMergeReconcileWriter()
            : new MsSqlDeleteInsertWriter();

        await ReloadAsync(
            writer, new ListSegment("Region", ["EU"]), targetTable: identityTable,
            targetColumns: TargetColumns(identity: true));

        var rows = await GetTargetRowsAsync(identityTable);
        Assert.Equal([41, 42], rows.Keys.Order());
    }

    // ---- Reader and auto expansion -----------------------------------------------------------

    [Fact]
    public async Task Reader_IgnoresThePreviousWatermarkAndEchoesItBackUnchanged()
    {
        await ExecuteAsync(_sourceConnection, $"""
            INSERT INTO dbo.[{_sourceTable}] (Id, Region, Name) VALUES (1, 'EU', 'One'), (2, 'EU', 'Two');
            """);

        var read = await _reader.ReadChangesAsync(
            _sourceConnection, Source(), previousWatermark: "999", ReadIntent.InitialLoad, [], "mapping", [], [], new Dictionary<string, string>(), CancellationToken.None);

        var rows = new List<ChangeRow>();
        await foreach (var row in read.Rows)
            rows.Add(row);

        // Every row is re-read despite the watermark, and every one is an Insert — a full scan can't
        // observe a deletion, so encoding delete semantics is the writer's job on a reload.
        Assert.Equal(2, rows.Count);
        Assert.All(rows, r => Assert.Equal(ChangeOperation.Insert, r.Operation));
        Assert.Equal("999", read.NewWatermark);
    }

    [Fact]
    public async Task Reader_ComposesTheSegmentWithTheMappingsOwnFilter()
    {
        await ExecuteAsync(_sourceConnection, $"""
            INSERT INTO dbo.[{_sourceTable}] (Id, Region, Name) VALUES
                (1, 'EU', 'keep'), (2, 'EU', 'skip'), (3, 'US', 'keep');
            """);

        var source = Source();
        source.Filter = "Name = 'keep'";

        var read = await _reader.ReadChangesAsync(
            _sourceConnection, source, null, ReadIntent.InitialLoad, [], "mapping", [], [], SegmentOptions(new ListSegment("Region", ["EU"])), CancellationToken.None);

        var ids = new List<object?>();
        await foreach (var row in read.Rows)
            ids.Add(row["Id"]);

        Assert.Equal([1], ids);
    }

    [Fact]
    public async Task ExpandAutoSegments_DividesTheObservedRangeAndCoversEveryRow()
    {
        await ExecuteAsync(_sourceConnection, $"""
            INSERT INTO dbo.[{_sourceTable}] (Id, Region, Name)
            SELECT n, 'EU', CONCAT('Row', n) FROM (VALUES (1),(2),(3),(4),(5),(6),(7),(8),(9),(10)) v(n);
            """);

        var expanded = await _reader.ExpandAutoSegmentsAsync(
            _sourceConnection, Source(), [new AutoSegment("Id", 3)], TargetColumns(), MappingName, CancellationToken.None);

        Assert.Equal(
            [new RangeSegment("Id", "1", "4"), new RangeSegment("Id", "4", "7"), new RangeSegment("Id", "7", "11")],
            expanded);

        // Running every produced segment reloads the whole table exactly once — the bucket bounds have
        // to cover MAX and not overlap, or this count comes out wrong.
        foreach (var segment in expanded)
            await ReloadAsync(new MsSqlDeleteInsertWriter(), segment);

        Assert.Equal(10, (await GetTargetRowsAsync()).Count);
    }

    /// <summary>
    /// Phase 187J, end to end against a real server: a relationship's foreign table is <c>LEFT JOIN</c>ed
    /// in, not inner-joined — a row whose foreign key matches gets the looked-up value, and a row whose
    /// foreign key has no match (including <c>NULL</c>) is still read, with the looked-up column
    /// <c>null</c> rather than the row silently disappearing.
    /// </summary>
    [Fact]
    public async Task Reader_WithARelationship_LeftJoinsTheForeignTable_KeepingUnmatchedRows()
    {
        var src = $"ReloadRelSrc_{Guid.NewGuid():N}";
        var lookup = $"ReloadRelLookup_{Guid.NewGuid():N}";
        await ExecuteAsync(_sourceConnection, $"""
            CREATE TABLE dbo.[{lookup}] (Id INT NOT NULL PRIMARY KEY, Label NVARCHAR(50) NOT NULL);
            """);
        await ExecuteAsync(_sourceConnection, $"""
            CREATE TABLE dbo.[{src}] (Id INT NOT NULL PRIMARY KEY, RegionId INT NULL);
            """);
        await ExecuteAsync(_sourceConnection, $"INSERT INTO dbo.[{lookup}] (Id, Label) VALUES (1, 'North');");
        await ExecuteAsync(_sourceConnection, $"""
            INSERT INTO dbo.[{src}] (Id, RegionId) VALUES (1, 1), (2, NULL), (3, 99);
            """);

        var relationships = new List<RelationshipConfig>
        {
            new()
            {
                Name = "region",
                Schema = "dbo",
                Table = lookup,
                JoinKeys = [new RelationshipJoinKey { LocalColumn = "RegionId", ForeignColumn = "Id" }],
            },
        };
        var mappings = new List<ColumnMapping>
        {
            new() { SourceColumn = "Id", TargetColumn = "Id" },
            new() { SourceColumn = "Label", TargetColumn = "RegionLabel", Relationship = "region" },
        };

        var read = await _reader.ReadChangesAsync(
            _sourceConnection, Source(src), previousWatermark: null, ReadIntent.InitialLoad, mappings,
            MappingName, [], relationships, new Dictionary<string, string>(), CancellationToken.None);

        var byId = new Dictionary<int, string?>();
        await foreach (var row in read.Rows)
            byId[(int)row["Id"]!] = (string?)row["Label"];

        Assert.Equal(3, byId.Count);
        Assert.Equal("North", byId[1]); // matched: the looked-up value comes through
        Assert.Null(byId[2]);           // RegionId is NULL: no match, row still present, looked-up column null
        Assert.Null(byId[3]);           // RegionId points at nothing: no match, row still present
    }

    [Fact]
    public async Task ExpandAutoSegments_LeavesOtherSegmentModesUntouched()
    {
        await ExecuteAsync(_sourceConnection, $"INSERT INTO dbo.[{_sourceTable}] (Id, Region, Name) VALUES (1, 'EU', 'One');");

        BatchReloadSegment list = new ListSegment("Region", ["EU"]);
        var expanded = await _reader.ExpandAutoSegmentsAsync(
            _sourceConnection, Source(), [list], TargetColumns(), MappingName, CancellationToken.None);

        Assert.Equal([list], expanded);
    }

    /// <summary>An empty source still has to reach a writer, or a reconciling reload never gets the
    /// chance to clear the target.</summary>
    [Fact]
    public async Task ExpandAutoSegments_OnAnEmptyTable_YieldsOneFullSegment()
    {
        var expanded = await _reader.ExpandAutoSegmentsAsync(
            _sourceConnection, Source(), [new AutoSegment("Id", 4)], TargetColumns(), MappingName, CancellationToken.None);

        Assert.Equal([new FullSegment()], expanded);
    }
}
