using ClrKernel.Core.Secrets;
using DataSync.Api.Services;
using DataSync.Core.Config;
using DataSync.Core.Git;
using DataSync.Drivers.Abstractions;
using LibGit2Sharp;
using Xunit;

namespace DataSync.Api.Tests;

/// <summary>
/// The mapping metadata cache — phase 90.
/// <para>
/// **The property this file exists to pin is that the cache moves only when somebody moved it.** The
/// audit this phase resolves found that every reader and writer re-queries the source's or target's
/// catalog on every pass, and found the reason: <c>ColumnMapping</c> deliberately stores no type or
/// key information, because freezing an inference risks running a <c>MERGE</c> against a primary key
/// that no longer exists. Caching that information at all is only safe if staleness stays a decision
/// somebody makes — so the assertions below are as much about what a save does *not* write as about
/// what Refresh does.
/// </para>
/// <para>
/// Nothing yet reads these fields. That is deliberate too: this phase builds the cache and the action
/// that refreshes it, and switching each run-time consumer onto it is a behaviour change with its own
/// later, separately-reviewed phase.
/// </para>
/// </summary>
public sealed class MappingMetadataTests : IDisposable
{
    private static readonly GitAuthor Author = new("Test", "test@example.com");

    private readonly string _root = Directory.CreateTempSubdirectory("datasync-mapping-metadata-").FullName;
    private readonly ConfigRepository _config;
    private readonly FakeColumnCatalog _catalog = new();
    private readonly MappingMetadataService _service;

    public MappingMetadataTests()
    {
        Repository.Init(_root);
        _config = new ConfigRepository(
            Path.Combine(_root, "config"), new GitCommitService(_root),
            SecretStore.ForProviders([new InMemorySecretProvider()]));
        _service = new MappingMetadataService(_config, _catalog);

        // Both endpoints on the replication, neither on the mapping — so every test below also
        // exercises the inheritance a refresh has to resolve before it knows what to introspect.
        _config.SaveReplicationTask(new ReplicationTaskConfig
        {
            Name = "r",
            Endpoints = new TaskEndpoints
            {
                Source = new EndpointRef { ConnectionName = "src", Database = "App" },
                Target = new EndpointRef { ConnectionName = "tgt", Database = "DW" },
            },
            Scheduling = new SchedulingConfig { Mode = ScheduleMode.Periodic },
            ChangeProcessing = new ChangeProcessingConfig
            {
                Reader = new ReaderConfig { Kind = "BatchReload" },
                Cache = new CacheConfig { Kind = "StagingTable" },
                Writer = new WriterConfig { Kind = "DeleteInsert" },
            },
        }, Author);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private static ColumnMetadata Col(
        string name, string type, bool nullable = false, bool pk = false, bool identity = false) =>
        new(name, type, nullable, pk, identity);

    private void SaveMapping(
        List<CachedColumn>? sourceColumns = null,
        List<CachedColumn>? targetColumns = null,
        DateTime? capturedUtc = null,
        string sourceTable = "Orders") =>
        _config.SaveTableMapping("r", new TableMappingConfig
        {
            Name = "m",
            Sources = [new SourceTableSpec { Schema = "dbo", Table = sourceTable }],
            Targets = [new TableSpec { Schema = "dbo", Table = "Orders" }],
            ColumnMappings = [new ColumnMapping { SourceColumn = "Id", TargetColumn = "Id" }],
            SourceColumns = sourceColumns ?? [],
            TargetColumns = targetColumns ?? [],
            ColumnsCapturedUtc = capturedUtc,
        }, Author);

    // ---- Refresh: the only thing that reads a catalog ------------------------------------------

    [Fact]
    public async Task Refresh_PopulatesBothSides_AndReportsEveryColumnAsAdded()
    {
        SaveMapping();
        _catalog.Set("src", "App", "dbo", "Orders",
            Col("Id", "int", pk: true, identity: true), Col("Region", "nvarchar(50)", nullable: true));
        _catalog.Set("tgt", "DW", "dbo", "Orders", Col("Id", "bigint", pk: true));

        var result = await _service.RefreshAsync("r", "m", Author, CancellationToken.None);

        Assert.Equal(2, result.Mapping.SourceColumns.Count);
        Assert.Single(result.Mapping.TargetColumns);
        Assert.NotNull(result.Mapping.ColumnsCapturedUtc);

        // A first capture is every column arriving, not "no change" — the cache went from saying
        // nothing to saying two things, and that is what the operator is being shown.
        Assert.Equal(["Id", "Region"], result.Source.Added);
        Assert.Empty(result.Source.Removed);
        Assert.Empty(result.Source.Changed);
        Assert.True(result.Source.Refreshed);

        // And it is on disk, not only in the response.
        Assert.Equal("nvarchar(50)", _config.LoadTableMapping("r", "m").SourceColumns[1].NativeType);
    }

    [Fact]
    public async Task Refresh_ReportsWhichColumnsWereAddedRemovedAndChanged_ByName()
    {
        SaveMapping(sourceColumns:
        [
            new CachedColumn("Id", "int", false, true, true),
            new CachedColumn("Region", "nvarchar(50)", true, false, false),
            new CachedColumn("Retired", "bit", true, false, false),
        ]);
        _catalog.Set("src", "App", "dbo", "Orders",
            Col("Id", "int", pk: true, identity: true),          // untouched
            Col("Region", "nvarchar(200)", nullable: true),      // widened
            Col("Currency", "char(3)"));                         // new
        _catalog.Set("tgt", "DW", "dbo", "Orders", Col("Id", "bigint", pk: true));

        var result = await _service.RefreshAsync("r", "m", Author, CancellationToken.None);

        // Named, not counted: a refresh reporting "1 changed" leaves the operator to go and find it.
        Assert.Equal(["Currency"], result.Source.Added);
        Assert.Equal(["Retired"], result.Source.Removed);
        Assert.Equal(["Region"], result.Source.Changed);
        Assert.Equal(3, result.Source.ColumnCount);
    }

    [Fact]
    public async Task Refresh_NoticesAKeyThatDisappeared_EvenThoughTheTypeIsUnchanged()
    {
        // The exact hazard the audit named: a writer building a MERGE against a primary key that is
        // no longer one. Nothing about the type moved, so a diff comparing only types would call
        // this "no change" and report the one schema change that matters as nothing at all.
        SaveMapping(sourceColumns: [new CachedColumn("Id", "int", false, true, false)]);
        _catalog.Set("src", "App", "dbo", "Orders", Col("Id", "int", pk: false));
        _catalog.Set("tgt", "DW", "dbo", "Orders", Col("Id", "int", pk: true));

        var result = await _service.RefreshAsync("r", "m", Author, CancellationToken.None);

        Assert.Equal(["Id"], result.Source.Changed);
        Assert.False(result.Mapping.SourceColumns[0].IsPrimaryKey);
    }

    [Fact]
    public async Task Refresh_ARenamedColumn_ReadsAsOneRemovalAndOneAddition()
    {
        // Rather than as a change. Nothing in a catalog row says the new name is the old one, and
        // pairing them by a matching type would pair two unrelated columns just as readily.
        SaveMapping(sourceColumns: [new CachedColumn("Region", "nvarchar(50)", true, false, false)]);
        _catalog.Set("src", "App", "dbo", "Orders", Col("Territory", "nvarchar(50)", nullable: true));
        _catalog.Set("tgt", "DW", "dbo", "Orders", Col("Id", "int", pk: true));

        var result = await _service.RefreshAsync("r", "m", Author, CancellationToken.None);

        Assert.Equal(["Territory"], result.Source.Added);
        Assert.Equal(["Region"], result.Source.Removed);
        Assert.Empty(result.Source.Changed);
    }

    [Fact]
    public async Task Refresh_ASideThatCannotBeRead_KeepsItsCachedColumns_AndSaysWhy()
    {
        // The one outcome an operator pressing Refresh cannot want: a target that provisioning has
        // yet to create is not a target with no columns, and emptying a usable picture over it would
        // be worse than the staleness the whole feature exists to manage.
        SaveMapping(targetColumns: [new CachedColumn("Id", "bigint", false, true, false)]);
        _catalog.Set("src", "App", "dbo", "Orders", Col("Id", "int", pk: true));
        _catalog.Missing.Add(("tgt", "DW", "dbo", "Orders"));

        var result = await _service.RefreshAsync("r", "m", Author, CancellationToken.None);

        Assert.True(result.Source.Refreshed);
        Assert.False(result.Target.Refreshed);
        Assert.Contains("was not found", result.Target.Unavailable);
        Assert.Equal("bigint", Assert.Single(result.Mapping.TargetColumns).NativeType);
        Assert.Empty(result.Target.Removed);
    }

    [Fact]
    public async Task Refresh_ASourceWithNoTable_IsAStatedReasonRatherThanAnError()
    {
        // A query-configured reader has no catalog entry to point at — a real configuration, not a
        // fault, and the refresh still does the target's side.
        SaveMapping(sourceTable: "");
        _catalog.Set("tgt", "DW", "dbo", "Orders", Col("Id", "int", pk: true));

        var result = await _service.RefreshAsync("r", "m", Author, CancellationToken.None);

        Assert.False(result.Source.Refreshed);
        Assert.Contains("query source", result.Source.Unavailable);
        Assert.True(result.Target.Refreshed);
        // And the source was never asked about — the point of checking before calling.
        Assert.DoesNotContain(_catalog.Calls, c => c.Connection == "src");
    }

    [Fact]
    public async Task Refresh_WhenNeitherSideCanBeRead_LeavesTheCaptureTimeAlone()
    {
        var captured = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        SaveMapping(
            sourceColumns: [new CachedColumn("Id", "int", false, true, false)], capturedUtc: captured);
        _catalog.Missing.Add(("src", "App", "dbo", "Orders"));
        _catalog.Missing.Add(("tgt", "DW", "dbo", "Orders"));

        var result = await _service.RefreshAsync("r", "m", Author, CancellationToken.None);

        // Stamping now would date a picture nobody took, and the timestamp is the only thing telling
        // an operator whether the cache is worth trusting.
        Assert.Equal(captured, result.Mapping.ColumnsCapturedUtc);
        Assert.Single(result.Mapping.SourceColumns);
    }

    [Fact]
    public async Task Refresh_AsksEachSideExactlyOnce()
    {
        SaveMapping();
        _catalog.Set("src", "App", "dbo", "Orders", Col("Id", "int", pk: true));
        _catalog.Set("tgt", "DW", "dbo", "Orders", Col("Id", "int", pk: true));

        await _service.RefreshAsync("r", "m", Author, CancellationToken.None);

        Assert.Equal(2, _catalog.Calls.Count);
    }

    [Fact]
    public async Task Refresh_OnAMappingThatIsNotThere_Throws()
    {
        await Assert.ThrowsAsync<FileNotFoundException>(
            () => _service.RefreshAsync("r", "nope", Author, CancellationToken.None));
    }

    // ---- Save: what an ordinary edit may and may not do to the cache ----------------------------

    private static TableMappingConfig Mapping(
        List<CachedColumn> source, List<CachedColumn> target, DateTime? capturedUtc = null) => new()
        {
            Name = "m",
            Sources = [new SourceTableSpec { Schema = "dbo", Table = "Orders" }],
            Targets = [new TableSpec { Schema = "dbo", Table = "Orders" }],
            SourceColumns = source,
            TargetColumns = target,
            ColumnsCapturedUtc = capturedUtc,
        };

    private static readonly DateTime Then = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Now = new(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void ASaveThatSendsTheSameColumnsBack_LeavesTheCaptureTimeWhereItWas()
    {
        // The requirement in one assertion: re-saving a mapping without touching either side's table
        // is not a capture, so the cache still says when it was actually read. The editor sends the
        // columns it is holding — which are the cached ones — and this is what makes that harmless.
        var stored = Mapping(
            [new CachedColumn("Id", "int", false, true, false)],
            [new CachedColumn("Id", "int", false, true, false)],
            Then);
        var incoming = Mapping(
            [new CachedColumn("Id", "int", false, true, false)],
            [new CachedColumn("Id", "int", false, true, false)]);

        MappingMetadataCapture.Apply(incoming, stored, Now);

        Assert.Equal(Then, incoming.ColumnsCapturedUtc);
    }

    [Fact]
    public void ASaveCarryingDifferentColumns_IsACapture_AndIsStamped()
    {
        var stored = Mapping([new CachedColumn("Id", "int", false, true, false)], [], Then);
        var incoming = Mapping(
            [new CachedColumn("Id", "int", false, true, false), new CachedColumn("Region", "nvarchar(50)", true, false, false)],
            []);

        MappingMetadataCapture.Apply(incoming, stored, Now);

        Assert.Equal(Now, incoming.ColumnsCapturedUtc);
        Assert.Equal(2, incoming.SourceColumns.Count);
    }

    [Fact]
    public void ASaveThatSendsNoColumnsAtAll_DoesNotClearWhatIsStored()
    {
        // A client written before these fields existed, or one whose catalog call failed this
        // minute, sends nothing. Reading that as "this table now has no columns" would destroy a
        // good picture over an edit to something else entirely — a note, a hook, a transform.
        var stored = Mapping(
            [new CachedColumn("Id", "int", false, true, false)],
            [new CachedColumn("Id", "bigint", false, true, false)],
            Then);
        var incoming = Mapping([], []);

        MappingMetadataCapture.Apply(incoming, stored, Now);

        Assert.Single(incoming.SourceColumns);
        Assert.Equal("bigint", Assert.Single(incoming.TargetColumns).NativeType);
        Assert.Equal(Then, incoming.ColumnsCapturedUtc);
    }

    [Fact]
    public void AReorderedColumnList_CountsAsAChange()
    {
        // Column order is part of what was captured, and a table whose columns were reordered is a
        // table whose picture moved — a positional consumer would read the same list differently.
        var stored = Mapping(
            [new CachedColumn("Id", "int", false, true, false), new CachedColumn("Region", "nvarchar(50)", true, false, false)],
            [], Then);
        var incoming = Mapping(
            [new CachedColumn("Region", "nvarchar(50)", true, false, false), new CachedColumn("Id", "int", false, true, false)],
            []);

        MappingMetadataCapture.Apply(incoming, stored, Now);

        Assert.Equal(Now, incoming.ColumnsCapturedUtc);
    }

    [Fact]
    public void ANewMappingWithNothingCaptured_GetsNoTimestampRatherThanNow()
    {
        // A mapping created against a source nobody could read is not a mapping captured at this
        // instant. Null is what "never captured" looks like, and the UI says so.
        var incoming = Mapping([], []);

        MappingMetadataCapture.Apply(incoming, stored: null, Now);

        Assert.Null(incoming.ColumnsCapturedUtc);
    }

    [Fact]
    public void ANewMappingCarryingTheEditorsColumns_IsCapturedOnCreation()
    {
        // Capture on creation, from the fetch the column-mapping tab already made: no catalog was
        // consulted here at all, which is the whole design — the editor holds the answer already.
        var incoming = Mapping(
            [new CachedColumn("Id", "int", false, true, false)],
            [new CachedColumn("Id", "int", false, true, false)]);

        MappingMetadataCapture.Apply(incoming, stored: null, Now);

        Assert.Equal(Now, incoming.ColumnsCapturedUtc);
        Assert.Empty(_catalog.Calls);
    }
}
