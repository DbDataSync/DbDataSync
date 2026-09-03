using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using DbDataSync.Api.Controllers;
using DbDataSync.Api.Services;
using DbDataSync.Core.Config;
using DbDataSync.Drivers.Abstractions;
using LibGit2Sharp;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace DbDataSync.Api.Tests;

/// <summary>
/// Points the API's column catalog at a fake, so a bulk create can be watched introspecting without a
/// database anywhere near it. Everything else — config, the git commit each save makes, endpoint
/// inheritance — is the real thing, which is the point: what is being tested is what the endpoint
/// writes, and only the part that would open a socket is replaced.
/// </summary>
public sealed class CatalogApiFactory : TestApiFactory
{
    public FakeColumnCatalog Catalog { get; } = new();

    protected override void ConfigureTestServices(IServiceCollection services)
    {
        services.RemoveAll<IColumnCatalog>();
        services.AddSingleton<IColumnCatalog>(Catalog);
    }
}

/// <summary>
/// A mapping created in bulk arrives runnable — phase 95.
/// <para>
/// **The property this file exists to pin is that nobody has to open a bulk-created mapping before it
/// can run.** It used to arrive with two independent reasons it could not: an empty metadata cache,
/// which phase 91 made throw, and an empty <c>ColumnMappings</c>, which staging has always thrown on.
/// Forty tables therefore meant forty visits to the mapping editor, which is the gesture the bulk
/// screen exists to remove. So the assertions below are about what a mapping *has* the moment the
/// batch answers, and about the batch surviving every way a side can fail to be read.
/// </para>
/// </summary>
public sealed class BulkMappingCaptureTests(CatalogApiFactory factory) : IClassFixture<CatalogApiFactory>
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly HttpClient _client = factory.CreateClient();

    /// <summary>A bulk-created mapping states only its tables, so the replication is what says where
    /// they live — and what capture has to resolve each side against.</summary>
    private static readonly TaskEndpoints Endpoints = new()
    {
        Source = new EndpointRef { ConnectionName = "prod-src", Database = "App" },
        Target = new EndpointRef { ConnectionName = "warehouse", Database = "DW" },
    };

    private static ColumnMetadata Col(string name, string type = "int", bool pk = false) =>
        new(name, type, false, pk, false);

    private async Task<string> EnsureReplicationAsync()
    {
        var name = $"repl-{Guid.NewGuid():N}";
        await _client.PutAsJsonAsync($"/api/replications/{name}", new ReplicationTaskConfig
        {
            Name = name,
            Endpoints = Endpoints,
            Scheduling = new SchedulingConfig { Mode = ScheduleMode.Continuous, FrequencySeconds = 30 },
            ChangeProcessing = new ChangeProcessingConfig
            {
                Reader = new ReaderConfig { Kind = "MsSqlChangeTracking" },
                Cache = new CacheConfig { Kind = "MsSqlStagingTable" },
                Writer = new WriterConfig { Kind = "MsSqlMerge" },
            },
        }, JsonOptions);
        return name;
    }

    private Task<HttpResponseMessage> BulkCreateAsync(string replication, params string[] tables) =>
        _client.PostAsJsonAsync(
            $"/api/replications/{replication}/table-mappings/bulk",
            new { tables = tables.Select(t => new { schema = "dbo", table = t }).ToArray() },
            JsonOptions);

    private Task<TableMappingConfig?> GetMappingAsync(string replication, string name) =>
        _client.GetFromJsonAsync<TableMappingConfig>(
            $"/api/replications/{replication}/table-mappings/{name}", JsonOptions);

    private static async Task<BulkResultDto> ResultOf(HttpResponseMessage response)
    {
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<BulkResultDto>(JsonOptions))!;
    }

    [Fact]
    public async Task ABulkCreatedMapping_HasItsColumnsCached_AndItsColumnsMapped()
    {
        var replication = await EnsureReplicationAsync();
        factory.Catalog.Set("prod-src", "App", "dbo", "Orders", Col("Id", "int", pk: true), Col("Total", "decimal"));
        factory.Catalog.Set("warehouse", "DW", "dbo", "Orders", Col("Id", "int", pk: true), Col("Total", "decimal"));

        var result = await ResultOf(await BulkCreateAsync(replication, "Orders"));
        Assert.Equal(["dbo.Orders"], result.Created);
        Assert.Empty(result.Notes);

        var mapping = await GetMappingAsync(replication, "dbo.Orders");

        Assert.Equal(["Id", "Total"], mapping!.SourceColumns.Select(c => c.Name));
        Assert.Equal(["Id", "Total"], mapping.TargetColumns.Select(c => c.Name));
        Assert.True(mapping.SourceColumns[0].IsPrimaryKey);
        Assert.NotNull(mapping.ColumnsCapturedUtc);

        Assert.Equal(["Id", "Total"], mapping.ColumnMappings.Select(c => c.SourceColumn));
        Assert.Equal(["Id", "Total"], mapping.ColumnMappings.Select(c => c.TargetColumn));

        // The type is inferred through the canonical system on every pass, deliberately — writing an
        // inference into config freezes today's answer against a column that later changes.
        Assert.All(mapping.ColumnMappings, c => Assert.Null(c.TargetType));
        Assert.All(mapping.ColumnMappings, c => Assert.Null(c.Transform));
        Assert.All(mapping.ColumnMappings, c => Assert.Empty(c.Renames));
    }

    /// <summary>
    /// One save per mapping, still. Capturing before the save rather than refreshing after it is the
    /// whole reason the read was lifted out of <c>MappingMetadataService</c> — a create-then-refresh
    /// would double both the writes and the git history of a forty-table batch.
    /// </summary>
    [Fact]
    public async Task CapturingCostsNoExtraCommit()
    {
        var replication = await EnsureReplicationAsync();
        factory.Catalog.Set("prod-src", "App", "dbo", "A", Col("Id"));
        factory.Catalog.Set("prod-src", "App", "dbo", "B", Col("Id"));

        using var repo = new Repository(factory.RepoRoot);
        var before = repo.Commits.Count();

        await ResultOf(await BulkCreateAsync(replication, "A", "B"));

        Assert.Equal(before + 2, repo.Commits.Count());
    }

    /// <summary>
    /// The ordinary case for a mapping created from a source table: the target is not there yet,
    /// because provisioning has not run. Every source column is mapped, which is what makes the batch
    /// runnable — <c>ProvisioningService</c> builds its <c>CREATE TABLE</c> from the column mappings,
    /// so what is mapped here is literally the table that will appear.
    /// </summary>
    [Fact]
    public async Task ATargetThatDoesNotExistYet_MapsEverySourceColumn_AndSaysWhy()
    {
        var replication = await EnsureReplicationAsync();
        factory.Catalog.Set("prod-src", "App", "dbo", "Fresh", Col("Id"), Col("Name", "nvarchar(50)"));

        var result = await ResultOf(await BulkCreateAsync(replication, "Fresh"));
        Assert.Equal(["dbo.Fresh"], result.Created);

        var note = Assert.Single(result.Notes);
        Assert.Equal("dbo.Fresh", note.Mapping);
        Assert.Equal("target", note.Side);
        Assert.Contains("does not exist yet", note.Reason);

        var mapping = await GetMappingAsync(replication, "dbo.Fresh");
        Assert.Equal(["Id", "Name"], mapping!.SourceColumns.Select(c => c.Name));
        Assert.Empty(mapping.TargetColumns);
        Assert.NotNull(mapping.ColumnsCapturedUtc);
        Assert.Equal(["Id", "Name"], mapping.ColumnMappings.Select(c => c.TargetColumn));
    }

    /// <summary>
    /// An existing target narrower than its source maps only what both have — the same answer the
    /// editor's "Auto-map by name" gives, which is the rule this deliberately shares. Worth pinning
    /// because it is the case where a mapping created in bulk and never looked at syncs fewer columns
    /// than the source has.
    /// </summary>
    [Fact]
    public async Task ATargetNarrowerThanItsSource_MapsOnlyTheColumnsBothHave()
    {
        var replication = await EnsureReplicationAsync();
        factory.Catalog.Set("prod-src", "App", "dbo", "Wide", Col("Id"), Col("Kept"), Col("Extra"));
        factory.Catalog.Set("warehouse", "DW", "dbo", "Wide", Col("Id"), Col("Kept"), Col("TargetOnly"));

        var result = await ResultOf(await BulkCreateAsync(replication, "Wide"));
        Assert.Empty(result.Notes);

        var mapping = await GetMappingAsync(replication, "dbo.Wide");
        Assert.Equal(["Id", "Kept"], mapping!.ColumnMappings.Select(c => c.SourceColumn));
    }

    /// <summary>
    /// A source that cannot be read leaves the mapping exactly as this endpoint left every mapping
    /// before phase 95 — created, uncaptured, needing one Refresh. Failing the batch over it would
    /// withhold a mapping that used to be created without complaint, so it is reported and the batch
    /// carries on to the tables after it.
    /// </summary>
    [Fact]
    public async Task ASourceThatCannotBeRead_StillCreatesTheMapping_AndDoesNotStopTheBatch()
    {
        var replication = await EnsureReplicationAsync();
        factory.Catalog.Missing.Add(("prod-src", "App", "dbo", "Broken"));
        factory.Catalog.Set("prod-src", "App", "dbo", "After", Col("Id"));

        var result = await ResultOf(await BulkCreateAsync(replication, "Broken", "After"));

        Assert.Equal(["dbo.Broken", "dbo.After"], result.Created);
        var note = Assert.Single(result.Notes, n => n.Mapping == "dbo.Broken" && n.Side == "source");
        Assert.Contains("Broken", note.Reason);

        var broken = await GetMappingAsync(replication, "dbo.Broken");
        Assert.Empty(broken!.SourceColumns);
        Assert.Empty(broken.ColumnMappings);
        Assert.Null(broken.ColumnsCapturedUtc);

        // The table after it in the batch is unaffected, which is the half that matters.
        var after = await GetMappingAsync(replication, "dbo.After");
        Assert.Equal(["Id"], after!.ColumnMappings.Select(c => c.SourceColumn));
    }

    /// <summary>
    /// Nothing invented from the target alone. A mapping whose source could not be read has no columns
    /// to read *from*, and mappings built out of the target's names would describe a read that cannot
    /// happen.
    /// </summary>
    [Fact]
    public async Task ASourceThatCannotBeRead_MapsNothing_EvenWhenTheTargetReadsFine()
    {
        var replication = await EnsureReplicationAsync();
        factory.Catalog.Missing.Add(("prod-src", "App", "dbo", "TargetOnly"));
        factory.Catalog.Set("warehouse", "DW", "dbo", "TargetOnly", Col("Id"), Col("Name"));

        await ResultOf(await BulkCreateAsync(replication, "TargetOnly"));

        var mapping = await GetMappingAsync(replication, "dbo.TargetOnly");
        Assert.Empty(mapping!.ColumnMappings);
        Assert.Equal(["Id", "Name"], mapping.TargetColumns.Select(c => c.Name));
    }

    /// <summary>Two reads per table and no more. Sequential per table is a deliberate cost — eighty
    /// catalog calls for forty tables — and a third call per side would be the kind of accident that
    /// only shows up on somebody's real catalog.</summary>
    [Fact]
    public async Task EachSideIsAskedExactlyOnce()
    {
        var replication = await EnsureReplicationAsync();
        factory.Catalog.Set("prod-src", "App", "dbo", "Once", Col("Id"));
        factory.Catalog.Calls.Clear();

        await ResultOf(await BulkCreateAsync(replication, "Once"));

        Assert.Equal(
            [("prod-src", "App", "dbo", "Once"), ("warehouse", "DW", "dbo", "Once")],
            factory.Catalog.Calls);
    }

    /// <summary>A table that already has a mapping is skipped, and skipping it does not go and read
    /// its catalogs — the mapping it would have captured into is not being written.</summary>
    [Fact]
    public async Task ASkippedTableIsNotIntrospected()
    {
        var replication = await EnsureReplicationAsync();
        factory.Catalog.Set("prod-src", "App", "dbo", "Twice", Col("Id"));

        await ResultOf(await BulkCreateAsync(replication, "Twice"));
        factory.Catalog.Calls.Clear();

        var second = await ResultOf(await BulkCreateAsync(replication, "Twice"));

        Assert.Equal(["dbo.Twice"], second.Skipped);
        Assert.Empty(factory.Catalog.Calls);
    }

    private sealed record BulkResultDto(
        List<string> Created, List<string> Skipped, List<BulkCreateNote> Notes);
}
