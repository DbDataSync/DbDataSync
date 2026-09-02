using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using DataSync.Api.Controllers;
using DataSync.Core.Config;
using DataSync.Core.Sql;
using DataSync.Drivers.Generic;
using DataSync.Drivers.MsSql;
using DataSync.State;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DataSync.Api.Tests;

/// <summary>
/// The bulk lag endpoint and the range it computes — see phase 86.
/// <para>
/// **The range's whole job is what it leaves out.** A mapping with no figure — an unsupported
/// reader, or a supported one that has not run — must not reach the arithmetic at all. Counted as
/// zero it would drag every replication's lowest to zero and report the ones that cannot answer as
/// the ones that are keeping up best, which is the reading an operator would most want to trust.
/// </para>
/// <para>
/// The ranking tests are over <see cref="ReplicationLag.From"/> directly rather than over the
/// endpoint, because what they are asserting is arithmetic on a set of mappings and building each of
/// those states through real config and real polling history would put four fixtures between the
/// test and the two lines it is about. The endpoint tests below then prove the same function is
/// actually the one wired to the route, over mappings whose states came from the real service.
/// </para>
/// </summary>
public sealed class ReplicationLagTests(TestApiFactory factory) : IClassFixture<TestApiFactory>
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly HttpClient _client = factory.CreateClient();

    private static MappingLag Exact(long ms) =>
        new(MsSqlDriverKinds.Cdc, Supported: true, ms, null, null);

    private static MappingLag Estimated(long ms) =>
        new(MsSqlDriverKinds.ChangeTracking, Supported: true, null, 12, ms);

    private static MappingLag Unsupported() =>
        new(GenericDriverKinds.Watermark, Supported: false, null, null, null);

    private static MappingLag NoDataYet() =>
        new(MsSqlDriverKinds.Cdc, Supported: true, null, null, null);

    private static ReplicationLag Range(params (string Name, MappingLag Lag)[] mappings) =>
        ReplicationLag.From(mappings.ToDictionary(m => m.Name, m => m.Lag, StringComparer.Ordinal));

    // ---- The range ------------------------------------------------------------------------------

    [Fact]
    public void TheRange_ExcludesUnsupportedAndDatalessMappingsRatherThanCountingThemAsZero()
    {
        var lag = Range(
            ("cdc", Exact(300_000)),
            ("batch", Unsupported()),
            ("fresh", NoDataYet()),
            ("other", Exact(900_000)));

        // Five minutes, not zero. Two of these four mappings have nothing to say about how far
        // behind they are, and a range that folded either in as zero would report this replication
        // as momentarily caught up on the strength of the mappings that cannot answer.
        Assert.Equal(300_000, lag.LowestLagMs);
        Assert.Equal(900_000, lag.HighestLagMs);

        // Excluded from the *range*, not from the payload: the tab draws a row per mapping and has
        // to say which of the four things each one is.
        Assert.Equal(4, lag.Mappings.Count);
    }

    [Fact]
    public void TheRange_RanksExactAndEstimatedFiguresAgainstEachOther()
    {
        // A CDC mapping four minutes behind and a Change Tracking one past its DMV window and an
        // hour behind. Different precisions, and the coalesce is what makes them comparable at all.
        var lag = Range(("cdc", Exact(240_000)), ("ct", Estimated(3_600_000)));

        Assert.Equal(240_000, lag.LowestLagMs);
        Assert.Equal(3_600_000, lag.HighestLagMs);
    }

    [Fact]
    public void TheRange_RanksAnEstimateBelowAnExactFigureWhenItIsSmaller()
    {
        // The same pair the other way round, because a comparison that only ever sees the exact
        // figure at one end would pass the test above by accident.
        var lag = Range(("cdc", Exact(3_600_000)), ("ct", Estimated(240_000)));

        Assert.Equal(240_000, lag.LowestLagMs);
        Assert.Equal(3_600_000, lag.HighestLagMs);
    }

    [Fact]
    public void TheRange_SaysWhenAnEstimateWentIntoIt()
    {
        // The coalesce is deliberate and phase 85 spent a shape keeping these two apart, so the
        // range has to be able to say it crossed the line — its error is the poll interval, and a
        // screen showing it as a fact would be promising a precision nothing established.
        Assert.False(Range(("cdc", Exact(1000)), ("b", Unsupported())).RangeIncludesEstimates);
        Assert.True(Range(("cdc", Exact(1000)), ("ct", Estimated(2000))).RangeIncludesEstimates);

        // The estimate that did not reach the range does not flag it either.
        Assert.False(Range(("fresh", NoDataYet()), ("cdc", Exact(1000))).RangeIncludesEstimates);
    }

    [Fact]
    public void TheRange_IsNullWhenNoMappingCanReportLag()
    {
        var lag = Range(("batch", Unsupported()), ("fresh", NoDataYet()));

        // Null at both ends, never zero — "nothing here can tell you" is not "caught up", and a
        // list column rendering 0s off this would be inventing a reading.
        Assert.Null(lag.LowestLagMs);
        Assert.Null(lag.HighestLagMs);
        Assert.False(lag.RangeIncludesEstimates);
    }

    [Fact]
    public void TheRange_OfASingleReportingMapping_IsThatMappingAtBothEnds()
    {
        var lag = Range(("cdc", Exact(5000)), ("batch", Unsupported()));

        Assert.Equal(5000, lag.LowestLagMs);
        Assert.Equal(5000, lag.HighestLagMs);
    }

    // ---- The endpoint ---------------------------------------------------------------------------

    [Fact]
    public async Task TheEndpoint_KeysEveryMappingByNameAndRangesOverOnlyTheOnesWithAFigure()
    {
        var (task, database) = await SetUpAsync();

        // 'orders' inherits the replication's Change Tracking reader and is 60 versions and a
        // quarter of an hour behind; 'audit' overrides to a reader with no database-wide counter at
        // all, so it has no lag to report and never will.
        SetWatermark(task, "orders", "40");
        RecordCheck(database, MsSqlDriverKinds.ChangeTracking, "40", Origin);
        RecordCheck(database, MsSqlDriverKinds.ChangeTracking, "100", Origin.AddMinutes(15));

        var body = await GetLagAsync(task.Name);

        Assert.Equal(2, body.Mappings.Count);
        Assert.True(body.Mappings["orders"].Supported);
        Assert.Equal(900_000, body.Mappings["orders"].EstimatedLagMs);
        Assert.False(body.Mappings["audit"].Supported);

        // The unsupported mapping is in the payload and out of the range, which is the whole
        // distinction — and the range says it rests on an estimate.
        Assert.Equal(900_000, body.LowestLagMs);
        Assert.Equal(900_000, body.HighestLagMs);
        Assert.True(body.RangeIncludesEstimates);
    }

    [Fact]
    public async Task TheEndpoint_ListsAMappingThatHasNeverRunRatherThanOmittingIt()
    {
        var (task, _) = await SetUpAsync();

        var body = await GetLagAsync(task.Name);

        // No watermark, no history: 'orders' is supported and has nothing yet. Present in the map,
        // because an absent key and a mapping reporting nothing would look identical to a screen
        // that has to render them differently.
        Assert.True(body.Mappings.ContainsKey("orders"));
        Assert.True(body.Mappings["orders"].Supported);
        Assert.Null(body.Mappings["orders"].ExactLagMs);
        Assert.Null(body.Mappings["orders"].EstimatedLagMs);
        Assert.Null(body.HighestLagMs);
    }

    [Fact]
    public async Task TheEndpointIsNotFoundForAReplicationThatDoesNotExist()
    {
        var response = await _client.GetAsync("/api/replications/nosuchreplication/lag");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ---- Fixture --------------------------------------------------------------------------------

    /// <summary>A fixed instant to hang history off, so a test states the distance it asserts rather
    /// than the clock it ran on.</summary>
    private static readonly DateTimeOffset Origin = new(2026, 3, 1, 12, 0, 0, TimeSpan.Zero);

    private async Task<ReplicationLag> GetLagAsync(string replicationName)
    {
        var response = await _client.GetAsync($"/api/replications/{replicationName}/lag");
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<ReplicationLag>(JsonOptions);
        Assert.NotNull(body);
        return body;
    }

    /// <summary>
    /// A Change Tracking replication with two mappings: one inheriting the reader, one overriding it
    /// to a reader with no lag capability. Two states in one replication is the point — a range is
    /// only interesting when there is something in it to leave out.
    /// <para>
    /// The source database is named uniquely per test because the history group is keyed by it and
    /// the fixture's state database is shared; two tests writing checks for one group would read
    /// each other's rows.
    /// </para>
    /// </summary>
    private async Task<(ReplicationTaskConfig Task, string Database)> SetUpAsync()
    {
        var suffix = Guid.NewGuid().ToString("N")[..12];
        var replicationName = $"biglag-{suffix}";
        var database = $"App_{suffix}";

        (await _client.PutAsJsonAsync("/api/connections/biglag-src", new ConnectionInput
        {
            Name = "biglag-src",
            DriverType = ConnectionDriverType.MsSql,
            Host = "localhost",
            Database = "App",
            AuthMode = AuthMode.SqlAuth,
            UserId = "sa",
            Password = "DataSync_Test_Pw1",
        }, JsonOptions)).EnsureSuccessStatusCode();

        (await _client.PutAsJsonAsync($"/api/replications/{replicationName}", new ReplicationTaskConfig
        {
            Name = replicationName,
            Endpoints = new TaskEndpoints
            {
                Source = new EndpointRef { ConnectionName = "biglag-src", Database = database },
                Target = new EndpointRef { ConnectionName = "biglag-src", Database = "DW" },
            },
            Scheduling = new SchedulingConfig { Mode = ScheduleMode.Continuous, FrequencySeconds = 3600 },
            ChangeProcessing = new ChangeProcessingConfig
            {
                Reader = new ReaderConfig { Kind = MsSqlDriverKinds.ChangeTracking, Options = [] },
                Cache = new CacheConfig { Kind = MsSqlDriverKinds.StagingTable },
                Writer = new WriterConfig { Kind = MsSqlDriverKinds.Merge },
            },
        }, JsonOptions)).EnsureSuccessStatusCode();

        (await _client.PutAsJsonAsync(
            $"/api/replications/{replicationName}/table-mappings/orders", new TableMappingConfig
            {
                Name = "orders",
                Sources = [new SourceTableSpec { Schema = "dbo", Table = "orders" }],
                Targets = [new TableSpec { Schema = "dbo", Table = "orders" }],
            }, JsonOptions)).EnsureSuccessStatusCode();

        // The watermark reader takes a column, and a mapping that does not name one is rejected —
        // this test wants a reader with no database-wide counter, not one that will not validate.
        (await _client.PutAsJsonAsync(
            $"/api/replications/{replicationName}/table-mappings/audit", new TableMappingConfig
            {
                Name = "audit",
                Sources = [new SourceTableSpec { Schema = "dbo", Table = "audit" }],
                Targets = [new TableSpec { Schema = "dbo", Table = "audit" }],
                ReaderOverride = new ReaderConfig
                {
                    Kind = GenericDriverKinds.Watermark,
                    Options = new Dictionary<string, string> { ["watermarkColumn"] = "ModifiedAt" },
                },
            }, JsonOptions)).EnsureSuccessStatusCode();

        return (
            factory.Services.GetRequiredService<ConfigRepository>().LoadReplicationTask(replicationName),
            database);
    }

    /// <summary>Writes a mapping's watermark under the key the lag service reads it by — built
    /// through the source's own dialect, so a change to WatermarkKey.Build cannot leave this passing
    /// against a key nothing else uses.</summary>
    private void SetWatermark(ReplicationTaskConfig task, string mappingName, string watermark)
    {
        var source = EndpointResolution.ResolveSource(
            task, new SourceTableSpec { Schema = "dbo", Table = mappingName });
        factory.Services.GetRequiredService<ChangeWatermarkStore>().SetWatermark(
            task.Name, mappingName, WatermarkKey.Build(source, MsSqlDialect.Instance), watermark);
    }

    /// <summary>
    /// One history row at a stated time — written straight to the table rather than through
    /// <c>ChangeCheckStore.Record</c>, which stamps <c>UtcNow</c>. Correctly: a check happens when it
    /// happens. These tests are about arithmetic over a sequence, and a sequence needs times a test
    /// chooses.
    /// </summary>
    private void RecordCheck(string database, string kind, string? value, DateTimeOffset checkedAt)
    {
        var db = factory.Services.GetRequiredService<StateDatabase>();
        db.Retry(() =>
        {
            using var connection = db.OpenConnection();
            using var cmd = db.Command(connection, """
                INSERT INTO ChangeCheckHistory (ConnectionName, SourceDatabase, SourceKind, Value, CheckedAtUtc, SourceTimeUtc)
                VALUES ($connection, $database, $kind, $value, $checkedAt, NULL);
                """);
            cmd.Bind(db, "connection", "biglag-src");
            cmd.Bind(db, "database", database);
            cmd.Bind(db, "kind", kind);
            cmd.Bind(db, "value", (object?)value ?? DBNull.Value);
            cmd.Bind(db, "checkedAt", checkedAt.ToString("O"));
            cmd.ExecuteNonQuery();
        });
    }
}
