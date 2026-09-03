using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using DbDataSync.Core.Config;
using DbDataSync.Drivers.Abstractions;

namespace DbDataSync.Api.Tests;

/// <summary>
/// Declared settings, checked at save. The SPA's own checks are a courtesy — config also arrives here
/// directly and through a git commit somebody made by hand.
/// </summary>
public sealed class ParameterCheckTests(TestApiFactory factory) : IClassFixture<TestApiFactory>
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly HttpClient _client = factory.CreateClient();

    private async Task<string> SaveConnectionAsync()
    {
        var name = $"param-{Guid.NewGuid():N}";
        (await _client.PutAsJsonAsync($"/api/connections/{name}", new ConnectionInput
        {
            Name = name,
            DriverType = ConnectionDriverType.MsSql,
            Host = "localhost",
            Database = "App",
            AuthMode = AuthMode.IntegratedAuth,
        }, JsonOptions)).EnsureSuccessStatusCode();
        return name;
    }

    private async Task<HttpResponseMessage> SaveReplicationAsync(
        string connectionName, string readerKind, Dictionary<string, string> readerOptions)
    {
        var name = $"param-repl-{Guid.NewGuid():N}";
        return await _client.PutAsJsonAsync($"/api/replications/{name}", new ReplicationTaskConfig
        {
            Name = name,
            Scheduling = new SchedulingConfig { Mode = ScheduleMode.Continuous, FrequencySeconds = 3600 },
            Endpoints = new TaskEndpoints
            {
                Source = new EndpointRef { ConnectionName = connectionName, Database = "App" },
                Target = new EndpointRef { ConnectionName = connectionName, Database = "App" },
            },
            ChangeProcessing = new ChangeProcessingConfig
            {
                Reader = new ReaderConfig { Kind = readerKind, Options = readerOptions },
                Cache = new CacheConfig { Kind = "MsSqlStagingTable" },
                Writer = new WriterConfig { Kind = "MsSqlMerge" },
            },
        }, JsonOptions);
    }

    [Fact]
    public async Task ADriverDeclaresWhatItsConnectionsTake_AndSaysWhichItIs()
    {
        var capabilities = await _client.GetFromJsonAsync<DriverCapabilities>(
            "/api/drivers/MsSql/capabilities", JsonOptions);

        // The settings a reader reads are declared beside the code that reads them.
        var changeTracking = Assert.Single(capabilities!.Readers, r => r.Kind == "MsSqlChangeTracking");
        Assert.Single(changeTracking.Parameters, p => p.Name == "snapshotIsolation" && p.Type == ParameterType.Bool);

        var watermark = Assert.Single(capabilities.Readers, r => r.Kind == "Watermark");
        Assert.Single(watermark.Parameters, p => p.Name == "watermarkColumn" && p.Required);
    }

    [Fact]
    public async Task AReplicationSupplyingWhatItsReaderDeclared_Saves()
    {
        var connectionName = await SaveConnectionAsync();

        var response = await SaveReplicationAsync(
            connectionName, "MsSqlChangeTracking", new() { ["snapshotIsolation"] = "true" });

        response.EnsureSuccessStatusCode();
    }

    /// <summary>
    /// The watermark reader cannot run without it, which an operator previously found out on the
    /// first pass rather than on the save that chose the Kind.
    /// </summary>
    [Fact]
    public async Task AReplicationMissingARequiredReaderSetting_IsRefusedNamingIt()
    {
        var connectionName = await SaveConnectionAsync();

        var response = await SaveReplicationAsync(connectionName, "Watermark", []);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("Watermark column", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task AReplicationSupplyingTheWrongTypeForASetting_IsRefused()
    {
        var connectionName = await SaveConnectionAsync();

        var response = await SaveReplicationAsync(
            connectionName, "MsSqlChangeTracking", new() { ["snapshotIsolation"] = "sometimes" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("sometimes", await response.Content.ReadAsStringAsync());
    }

    /// <summary>
    /// A misspelled key used to be accepted and then quietly ignored at run time, which is the same
    /// experience as the setting not working.
    /// </summary>
    [Fact]
    public async Task AReplicationSupplyingASettingNobodyDeclared_IsRefused()
    {
        var connectionName = await SaveConnectionAsync();

        var response = await SaveReplicationAsync(
            connectionName, "MsSqlChangeTracking", new() { ["snapshotIsolatoin"] = "true" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("snapshotIsolatoin", await response.Content.ReadAsStringAsync());
    }

    // ---- Per-mapping pipeline overrides (phase 68) ----

    private async Task<string> SaveReplicationForMappingsAsync(string connectionName)
    {
        var name = $"param-repl-{Guid.NewGuid():N}";
        (await _client.PutAsJsonAsync($"/api/replications/{name}", new ReplicationTaskConfig
        {
            Name = name,
            Scheduling = new SchedulingConfig { Mode = ScheduleMode.Continuous, FrequencySeconds = 3600 },
            Endpoints = new TaskEndpoints
            {
                Source = new EndpointRef { ConnectionName = connectionName, Database = "App" },
                Target = new EndpointRef { ConnectionName = connectionName, Database = "App" },
            },
            ChangeProcessing = new ChangeProcessingConfig
            {
                Reader = new ReaderConfig { Kind = "MsSqlChangeTracking" },
                Cache = new CacheConfig { Kind = "MsSqlStagingTable" },
                Writer = new WriterConfig { Kind = "MsSqlMerge" },
            },
        }, JsonOptions)).EnsureSuccessStatusCode();
        return name;
    }

    private Task<HttpResponseMessage> SaveMappingAsync(
        string replicationName, Action<TableMappingConfig> configure)
    {
        var mapping = new TableMappingConfig
        {
            Name = "dbo.Orders",
            Sources = [new SourceTableSpec { Table = "Orders" }],
            // A different table from the source's: a historizing writer pointed at what it reads is
            // refused by its own check, which is not what these are about.
            Targets = [new TableSpec { Table = "OrdersHistory" }],
            ColumnMappings = [new ColumnMapping { SourceColumn = "Id", TargetColumn = "Id" }],
        };
        configure(mapping);
        return _client.PutAsJsonAsync(
            $"/api/replications/{replicationName}/table-mappings/{mapping.Name}", mapping, JsonOptions);
    }

    [Fact]
    public async Task AMappingOverridingAStageWithWhatThatKindDeclared_Saves()
    {
        var replicationName = await SaveReplicationForMappingsAsync(await SaveConnectionAsync());

        var response = await SaveMappingAsync(replicationName, m =>
            m.ReaderOverride = new ReaderConfig { Kind = "Watermark", Options = { ["watermarkColumn"] = "ModifiedAt" } });

        response.EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task AMappingOverrideMissingARequiredSetting_IsRefusedNamingTheMapping()
    {
        var replicationName = await SaveReplicationForMappingsAsync(await SaveConnectionAsync());

        var response = await SaveMappingAsync(replicationName, m =>
            m.ReaderOverride = new ReaderConfig { Kind = "Watermark" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("dbo.Orders", body);
        Assert.Contains("Watermark column", body);
    }

    [Fact]
    public async Task AMappingOverrideSupplyingASettingNobodyDeclared_IsRefused()
    {
        var replicationName = await SaveReplicationForMappingsAsync(await SaveConnectionAsync());

        var response = await SaveMappingAsync(replicationName, m =>
            m.WriterOverride = new WriterConfig { Kind = "Scd2", Options = { ["naturlKey"] = "Id" } });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("naturlKey", await response.Content.ReadAsStringAsync());
    }

    /// <summary>
    /// An override exists only to name something other than what would otherwise run. Naming something
    /// that cannot run makes the mapping silently unrunnable, so unlike at the replication level this
    /// is refused rather than left to the first pass.
    /// </summary>
    [Fact]
    public async Task AMappingOverrideNamingAKindTheDriverDoesNotOffer_IsRefused()
    {
        var replicationName = await SaveReplicationForMappingsAsync(await SaveConnectionAsync());

        var response = await SaveMappingAsync(replicationName, m =>
            m.WriterOverride = new WriterConfig { Kind = "NoSuchWriter" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("NoSuchWriter", await response.Content.ReadAsStringAsync());
    }

    /// <summary>
    /// The SCD Type 2 writer's natural key is auto-derived from the source's primary key since phase
    /// 68, so an override that states no key at all is a legitimate saved configuration.
    /// </summary>
    [Fact]
    public async Task AnScd2WriterOverrideStatingNoNaturalKey_Saves()
    {
        var replicationName = await SaveReplicationForMappingsAsync(await SaveConnectionAsync());

        var response = await SaveMappingAsync(replicationName, m =>
            m.WriterOverride = new WriterConfig { Kind = "Scd2" });

        response.EnsureSuccessStatusCode();
    }

    /// <summary>
    /// And so is a replication whose writer is SCD Type 2 — where, since phase 68, a natural key
    /// cannot be stated at all.
    /// </summary>
    [Fact]
    public async Task AReplicationUsingTheScd2WriterWithNoNaturalKey_Saves()
    {
        var connectionName = await SaveConnectionAsync();
        var name = $"param-repl-{Guid.NewGuid():N}";

        var response = await _client.PutAsJsonAsync($"/api/replications/{name}", new ReplicationTaskConfig
        {
            Name = name,
            Scheduling = new SchedulingConfig { Mode = ScheduleMode.Continuous, FrequencySeconds = 3600 },
            Endpoints = new TaskEndpoints
            {
                Source = new EndpointRef { ConnectionName = connectionName, Database = "App" },
                Target = new EndpointRef { ConnectionName = connectionName, Database = "App" },
            },
            ChangeProcessing = new ChangeProcessingConfig
            {
                Reader = new ReaderConfig { Kind = "MsSqlChangeTracking" },
                Cache = new CacheConfig { Kind = "MsSqlStagingTable" },
                Writer = new WriterConfig { Kind = "Scd2" },
            },
        }, JsonOptions);

        response.EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task AMappingThatOverridesNothing_IsNotCheckedAtAll()
    {
        var replicationName = await SaveReplicationForMappingsAsync(await SaveConnectionAsync());

        var response = await SaveMappingAsync(replicationName, _ => { });

        response.EnsureSuccessStatusCode();
    }

    /// <summary>
    /// A replication whose endpoints are not filled in yet names no driver, so there is nothing to
    /// check against — and refusing to save it would mean the endpoints could never be filled in.
    /// </summary>
    [Fact]
    public async Task AReplicationWithNoEndpointsYet_IsNotCheckedAgainstADriverItHasNotChosen()
    {
        var name = $"param-repl-{Guid.NewGuid():N}";

        var response = await _client.PutAsJsonAsync($"/api/replications/{name}", new ReplicationTaskConfig
        {
            Name = name,
            Scheduling = new SchedulingConfig { Mode = ScheduleMode.Continuous, FrequencySeconds = 3600 },
            ChangeProcessing = new ChangeProcessingConfig
            {
                Reader = new ReaderConfig { Kind = "Watermark" },
                Cache = new CacheConfig { Kind = "MsSqlStagingTable" },
                Writer = new WriterConfig { Kind = "MsSqlMerge" },
            },
        }, JsonOptions);

        response.EnsureSuccessStatusCode();
    }
}
