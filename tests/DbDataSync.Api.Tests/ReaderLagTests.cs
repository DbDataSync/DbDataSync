using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using DbDataSync.Api.Controllers;
using DbDataSync.Api.Services;
using DbDataSync.Core.Config;
using DbDataSync.Core.Sql;
using DbDataSync.Drivers.Generic;
using DbDataSync.Drivers.MsSql;
using DbDataSync.State;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DbDataSync.Api.Tests;

/// <summary>
/// Reader lag — phase 85's three figures over two mechanisms, computed since phase 87 entirely out of
/// the state database.
/// <para>
/// **The property this file exists to pin is now a fact about the constructor.** <c>ReaderLagService</c>
/// has no <c>IChangeCounterSource</c> to call and no <c>async</c> method to await, so "a status screen
/// must not become a second poller of the source" is not a rule these tests check but a shape they
/// could not violate if they tried. What they check instead is that every figure still comes out
/// right when both halves of every subtraction are read back from where they were cached — and that
/// the figure is absent, rather than reconstructed from whatever else is at hand, when one of them
/// was never written.
/// </para>
/// <para>
/// **Two caches, filled at two different moments.** The group's current position and its commit time
/// arrive on the polling gate's own tick (<c>ChangeCheckHistory</c>, phase 85 for CDC and phase 87 for
/// Change Tracking). The mapping's own applied position and *its* commit time arrive on the pass that
/// stored the watermark (<c>ChangeWatermarks.WatermarkTimeUtc</c>, phase 87). A test writes each
/// directly, and a test that leaves one out is choosing the fallback path the way a real deployment
/// chooses it: a mapping that has not run since the column existed, or a gate tick whose capture
/// failed.
/// </para>
/// <para>
/// **What every one of these is really asserting is that nothing here is measured against now.** The
/// definition these replace — now, minus the time of the last change we applied — is correct on a
/// busy source and wrong in the only case that matters: on a quiet, fully caught-up replication it
/// climbs for ever, and an alarm built on it goes off when there is nothing to do. Each figure below
/// has the source's own last-known position on the other side of the subtraction instead, which is
/// why <c>OnAQuietCaughtUpSource</c> can assert zero against history written an hour ago.
/// </para>
/// </summary>
public sealed class ReaderLagTests(TestApiFactory factory) : IClassFixture<TestApiFactory>
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly HttpClient _client = factory.CreateClient();

    private static string Lsn(params byte[] bytes) => MsSqlCdcCatalog.ToWatermark(bytes);

    /// <summary>A fixed instant to hang every history sequence off, so a test states the distances it
    /// is asserting rather than the clock it ran on.</summary>
    private static readonly DateTimeOffset Origin = new(2026, 3, 1, 12, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// The service under test, built from the two state stores and nothing else — which is the whole
    /// of its dependency list since phase 87 removed the counter source from it.
    /// </summary>
    private ReaderLagService BuildLag() => new(
        factory.Services.GetRequiredService<ChangeSourceResolver>(),
        factory.Services.GetRequiredService<ChangeWatermarkStore>(),
        factory.Services.GetRequiredService<ChangeCheckStore>(),
        NullLogger<ReaderLagService>.Instance);

    /// <summary>
    /// A replication with one mapping, on a source database named uniquely per test.
    /// <para>
    /// Unique because the history group is keyed by it and the fixture's state database is shared: two
    /// tests writing checks for one group would read each other's rows, and the failure would look
    /// like an ordering bug in the crossing scan rather than like the test collision it is.
    /// </para>
    /// </summary>
    private async Task<(ReplicationTaskConfig Task, string Database)> SetUpAsync(
        string readerKind, Dictionary<string, string>? readerOptions = null)
    {
        var suffix = Guid.NewGuid().ToString("N")[..12];
        var replicationName = $"lag-{suffix}";
        var database = $"App_{suffix}";

        (await _client.PutAsJsonAsync("/api/connections/lag-src", new ConnectionInput
        {
            Name = "lag-src",
            DriverType = ConnectionDriverType.MsSql,
            Host = "localhost",
            Database = "App",
            AuthMode = AuthMode.SqlAuth,
            UserId = "sa",
            Password = "DbDataSync_Test_Pw1",
        }, JsonOptions)).EnsureSuccessStatusCode();

        (await _client.PutAsJsonAsync($"/api/replications/{replicationName}", new ReplicationTaskConfig
        {
            Name = replicationName,
            Endpoints = new TaskEndpoints
            {
                Source = new EndpointRef { ConnectionName = "lag-src", Database = database },
                Target = new EndpointRef { ConnectionName = "lag-src", Database = "DW" },
            },
            Scheduling = new SchedulingConfig { Mode = ScheduleMode.Continuous, FrequencySeconds = 3600 },
            ChangeProcessing = new ChangeProcessingConfig
            {
                Reader = new ReaderConfig { Kind = readerKind, Options = readerOptions ?? [] },
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

        return (
            factory.Services.GetRequiredService<ConfigRepository>().LoadReplicationTask(replicationName),
            database);
    }

    /// <summary>
    /// Writes the mapping's watermark, and the source's own time for it, under the key the lag service
    /// will read them by — built through the source's own dialect, so a change to
    /// <c>WatermarkKey.Build</c> cannot leave these passing against a key nothing else uses.
    /// </summary>
    /// <param name="watermarkTime">
    /// What the reader mapped this position to on the pass that stored it. **Omitting it is a test
    /// choosing the uncached case**, which in production is a row written before phase 87 added the
    /// column, a reader with no position-to-time mapping, or an engine that declined to place the
    /// position — three causes, one answer.
    /// </param>
    private void SetWatermark(
        ReplicationTaskConfig task, string watermark, DateTimeOffset? watermarkTime = null)
    {
        var source = EndpointResolution.ResolveSource(
            task, new SourceTableSpec { Schema = "dbo", Table = "orders" });
        factory.Services.GetRequiredService<ChangeWatermarkStore>().SetWatermark(
            task.Name, "orders", WatermarkKey.Build(source, MsSqlDialect.Instance), watermark,
            watermarkTime);
    }

    /// <summary>
    /// One history row, at a stated time.
    /// <para>
    /// Written straight to the table rather than through <c>ChangeCheckStore.Record</c>, which stamps
    /// <c>UtcNow</c> — correctly, because a check happens when it happens. These tests are about the
    /// arithmetic over a sequence of them, and a sequence needs times a test chooses.
    /// </para>
    /// </summary>
    private void RecordCheck(
        string database, string kind, string? value, DateTimeOffset checkedAt,
        DateTimeOffset? sourceTime = null)
    {
        var db = factory.Services.GetRequiredService<StateDatabase>();
        db.Retry(() =>
        {
            using var connection = db.OpenConnection();
            using var cmd = db.Command(connection, """
                INSERT INTO ChangeCheckHistory (ConnectionName, SourceDatabase, SourceKind, Value, CheckedAtUtc, SourceTimeUtc)
                VALUES ($connection, $database, $kind, $value, $checkedAt, $sourceTime);
                """);
            cmd.Bind(db, "connection", "lag-src");
            cmd.Bind(db, "database", database);
            cmd.Bind(db, "kind", kind);
            cmd.Bind(db, "value", (object?)value ?? DBNull.Value);
            cmd.Bind(db, "checkedAt", checkedAt.ToString("O"));
            cmd.Bind(db, "sourceTime", (object?)sourceTime?.ToString("O") ?? DBNull.Value);
            cmd.ExecuteNonQuery();
        });
    }

    // ---- CDC ----------------------------------------------------------------------------------

    [Fact]
    public async Task CdcLag_IsTheDistanceBetweenTheMappingsPositionAndTheSourcesLastKnownOne()
    {
        var (task, database) = await SetUpAsync(MsSqlDriverKinds.Cdc);

        // The mapping's own position, and the time its own pass mapped that position to: 11:55. The
        // source had reached 12:00 when the gate last polled it. Five minutes behind, both ends from
        // fn_cdc_map_lsn_to_time on the same server, neither asked for here.
        SetWatermark(task, Lsn(0, 0, 0, 42, 0, 0, 0, 100, 0, 1), Origin - TimeSpan.FromMinutes(5));
        RecordCheck(database, MsSqlDriverKinds.Cdc, Lsn(0, 0, 0, 42, 0, 0, 0, 200, 0, 1),
            Origin, sourceTime: Origin);

        var result = BuildLag().Describe(task, "orders");

        Assert.True(result.Supported);
        Assert.Equal(TimeSpan.FromMinutes(5), result.ExactLag);
        Assert.Null(result.ExactVersionsBehind);
        Assert.Null(result.EstimatedLag);
    }

    [Fact]
    public async Task CdcLag_OnAQuietCaughtUpSource_DoesNotGrowWithTheWallClock()
    {
        var (task, database) = await SetUpAsync(MsSqlDriverKinds.Cdc);
        var applied = Lsn(0, 0, 0, 42, 0, 0, 0, 200, 0, 1);

        // The bug the naive definition would have shipped, written as a test: the last poll was an
        // hour ago and the source has not moved since. Against wall-clock now this reads as an hour
        // behind. Against the source's own position it is what it is — caught up.
        var lastPoll = DateTimeOffset.UtcNow - TimeSpan.FromHours(1);
        SetWatermark(task, applied, lastPoll);
        RecordCheck(database, MsSqlDriverKinds.Cdc, applied, lastPoll, sourceTime: lastPoll);

        Assert.Equal(TimeSpan.Zero, BuildLag().Describe(task, "orders").ExactLag);
    }

    [Fact]
    public async Task CdcLag_ClampsAMappingAheadOfTheLastPollToZero()
    {
        var (task, database) = await SetUpAsync(MsSqlDriverKinds.Cdc);

        // Legitimate, not a corruption: a pass ran between two polls and read past the position the
        // earlier one recorded, so its cached time is *later* than the group's. Negative is not a
        // lag, and reporting one would be a UI showing a replication as "-90s behind".
        SetWatermark(task, Lsn(0, 0, 0, 42, 0, 0, 1, 44, 0, 1), Origin + TimeSpan.FromMinutes(90));
        RecordCheck(database, MsSqlDriverKinds.Cdc, Lsn(0, 0, 0, 42, 0, 0, 0, 100, 0, 1),
            Origin, sourceTime: Origin);

        Assert.Equal(TimeSpan.Zero, BuildLag().Describe(task, "orders").ExactLag);
    }

    [Fact]
    public async Task CdcLag_MeasuresAgainstTheLatestCheckThatCarriesATime()
    {
        var (task, database) = await SetUpAsync(MsSqlDriverKinds.Cdc);
        SetWatermark(task, Lsn(0, 0, 0, 42, 0, 0, 0, 100, 0, 1), Origin - TimeSpan.FromMinutes(5));

        RecordCheck(database, MsSqlDriverKinds.Cdc, Lsn(0, 0, 0, 42, 0, 0, 0, 200, 0, 1),
            Origin, sourceTime: Origin);

        // A later poll that found no position at all — the capture job stopped. Taking the latest row
        // would report nothing and hide a lag that is growing for exactly that reason; taking the
        // latest row that *has* a time keeps the figure alive on the last reading there was.
        RecordCheck(database, MsSqlDriverKinds.Cdc, value: null, Origin.AddMinutes(30));

        Assert.Equal(TimeSpan.FromMinutes(5), BuildLag().Describe(task, "orders").ExactLag);
    }

    [Fact]
    public async Task CdcLag_ReportsNoDataYetRatherThanFetchingWhenNoCheckCarriesASourceTime()
    {
        var (task, database) = await SetUpAsync(MsSqlDriverKinds.Cdc);
        SetWatermark(task, Lsn(0, 0, 0, 42, 0, 0, 0, 100, 0, 1), Origin - TimeSpan.FromMinutes(2));

        // A row exists, but from before the capture job had produced a position at all — so it has no
        // time on it, and the group has nothing usable to measure against. The fresh install case,
        // which phase 85 answered with a live fetch through IChangeCounterSource. Phase 87 removed
        // that branch: a first install resolves itself on the next gate tick, and keeping the branch
        // meant every status screen retained a path to the source for the sake of one tick's wait.
        RecordCheck(database, MsSqlDriverKinds.Cdc, value: null, Origin);

        var result = BuildLag().Describe(task, "orders");

        // Supported, and no figure yet — the same state a mapping that has never read reports, not an
        // error and not a number.
        Assert.True(result.Supported);
        Assert.Null(result.ExactLag);
    }

    [Fact]
    public async Task CdcLag_ReportsNoDataYetForAWatermarkStoredBeforeTheCacheExisted()
    {
        var (task, database) = await SetUpAsync(MsSqlDriverKinds.Cdc);

        // A position with no cached time: the mapping last ran before phase 87 added the column, and
        // nothing backfills it — there is nothing to backfill it *from*. The group's own side is
        // fully populated, so this isolates the mapping's half of the subtraction.
        SetWatermark(task, Lsn(0, 0, 0, 42, 0, 0, 0, 100, 0, 1));
        RecordCheck(database, MsSqlDriverKinds.Cdc, Lsn(0, 0, 0, 42, 0, 0, 0, 200, 0, 1),
            Origin, sourceTime: Origin);

        var result = BuildLag().Describe(task, "orders");

        // Not an exception, not a live lookup, and — the point — not a figure invented from the one
        // end that is available. One pass from now this mapping reports normally.
        Assert.True(result.Supported);
        Assert.Null(result.ExactLag);
    }

    [Fact]
    public async Task CdcLag_IsNullBeforeTheMappingHasEverRead()
    {
        var (task, database) = await SetUpAsync(MsSqlDriverKinds.Cdc);
        RecordCheck(database, MsSqlDriverKinds.Cdc, Lsn(0, 0, 0, 42, 0, 0, 0, 200, 0, 1),
            Origin, sourceTime: Origin);

        // Supported, and unknown. A first pass has no position to be behind from, and a big number
        // there would read as an alarm about a replication that has simply not started.
        var result = BuildLag().Describe(task, "orders");

        Assert.True(result.Supported);
        Assert.Null(result.ExactLag);
    }

    // ---- Change Tracking, exact -----------------------------------------------------------------

    [Fact]
    public async Task ChangeTrackingExact_IsTheVersionDifference_FromTwoStoredNumbers()
    {
        var (task, database) = await SetUpAsync(MsSqlDriverKinds.ChangeTracking);
        SetWatermark(task, "40");
        RecordCheck(database, MsSqlDriverKinds.ChangeTracking, "100", Origin);

        var result = BuildLag().Describe(task, "orders");

        // Sixty versions, exact by construction: both numbers were already stored, neither is
        // estimated, and no time is claimed about them — what a version is worth depends entirely on
        // how often the source's tables are written to.
        Assert.Equal(60, result.ExactVersionsBehind);
        Assert.Null(result.ExactLag);
    }

    [Fact]
    public async Task ChangeTrackingExact_ClampsAMappingAheadOfTheLastPollToZero()
    {
        var (task, database) = await SetUpAsync(MsSqlDriverKinds.ChangeTracking);
        SetWatermark(task, "140");
        RecordCheck(database, MsSqlDriverKinds.ChangeTracking, "100", Origin);

        Assert.Equal(0, BuildLag().Describe(task, "orders").ExactVersionsBehind);
    }

    // ---- Change Tracking, exact time from two cached dm_tran_commit_table answers ----------------

    [Fact]
    public async Task ChangeTrackingTime_IsExactWhenBothCommitTimesWereCaptured()
    {
        var (task, database) = await SetUpAsync(MsSqlDriverKinds.ChangeTracking);

        // A polling sequence that would produce a 40-minute *estimate* if it were consulted. It is
        // not, because both commit times were captured — the mapping's by the pass that stored
        // version 40, the group's by the gate tick that saw version 100 — and 25 minutes is what
        // actually elapsed between the two commits.
        SetWatermark(task, "40", Origin.AddMinutes(5));
        RecordCheck(database, MsSqlDriverKinds.ChangeTracking, "10", Origin);
        RecordCheck(database, MsSqlDriverKinds.ChangeTracking, "100", Origin.AddMinutes(40),
            sourceTime: Origin.AddMinutes(30));

        var result = BuildLag().Describe(task, "orders");

        // Exact, in the same field CDC's exact figure arrives in, because it is exact in the same
        // sense: both ends are times the engine itself stated for those versions.
        Assert.Equal(TimeSpan.FromMinutes(25), result.ExactLag);
        Assert.Equal(60, result.ExactVersionsBehind);

        // And the estimate is absent, not merely different. A consumer must not have to know which
        // of two populated fields is the better one.
        Assert.Null(result.EstimatedLag);
    }

    [Fact]
    public async Task ChangeTrackingTime_FallsBackToTheEstimateWhenTheMappingsOwnTimeWasNeverCached()
    {
        var (task, database) = await SetUpAsync(MsSqlDriverKinds.ChangeTracking);

        // The mapping last ran before its commit time had anywhere to be stored. The gate's side is
        // fully populated, and it is still not enough: half an exact answer is not an exact answer.
        SetWatermark(task, "40");
        RecordCheck(database, MsSqlDriverKinds.ChangeTracking, "25", Origin);
        RecordCheck(database, MsSqlDriverKinds.ChangeTracking, "55", Origin.AddMinutes(20));
        RecordCheck(database, MsSqlDriverKinds.ChangeTracking, "100", Origin.AddMinutes(60),
            sourceTime: Origin.AddMinutes(60));

        var result = BuildLag().Describe(task, "orders");

        Assert.Null(result.ExactLag);
        Assert.Equal(TimeSpan.FromMinutes(40), result.EstimatedLag);
    }

    [Fact]
    public async Task ChangeTrackingTime_NeverMixesAnEngineTimeWithAPollTime()
    {
        var (task, database) = await SetUpAsync(MsSqlDriverKinds.ChangeTracking);

        // The other half of the same window: the mapping's version is placed and the group's current
        // one is not — a gate tick whose DMV lookup came back empty, or a row written before the gate
        // captured this mechanism's time at all. Taking the half that is available would mean
        // subtracting an engine-stated commit time from the wall-clock instant a poll happened to
        // run — a figure whose error is the polling interval, reported in the field that promises
        // there is none. Both ends from the engine, or the estimate, which at least owns its error.
        SetWatermark(task, "40", Origin.AddMinutes(10));
        RecordCheck(database, MsSqlDriverKinds.ChangeTracking, "40", Origin);
        RecordCheck(database, MsSqlDriverKinds.ChangeTracking, "100", Origin.AddMinutes(50));

        var result = BuildLag().Describe(task, "orders");

        Assert.Null(result.ExactLag);
        Assert.Equal(TimeSpan.FromMinutes(50), result.EstimatedLag);
    }

    [Fact]
    public async Task TheEndpointReportsAnExactChangeTrackingTimeInTheExactField()
    {
        var (task, database) = await SetUpAsync(MsSqlDriverKinds.ChangeTracking);
        SetWatermark(task, "40", Origin.AddMinutes(1));
        RecordCheck(database, MsSqlDriverKinds.ChangeTracking, "100", Origin,
            sourceTime: Origin.AddMinutes(21));

        var body = MappingLag.From(BuildLag().Describe(task, "orders"));

        // The distinction the shape exists to keep, from the other side: the same mechanism that
        // produced an estimate in TheEndpointReportsTheThreeFiguresUnderThreeSeparateNames produces
        // an exact figure here, and the two arrive under different names rather than under one name
        // with a qualifier beside it.
        Assert.Equal(1_200_000, body.ExactLagMs);
        Assert.Null(body.EstimatedLagMs);
        Assert.Equal(60, body.VersionsBehind);
    }

    // ---- Change Tracking, the estimated fallback ------------------------------------------------

    [Fact]
    public async Task ChangeTrackingEstimated_AnchorsOnTheCheckThatFirstReachedTheMappingsVersion()
    {
        var (task, database) = await SetUpAsync(MsSqlDriverKinds.ChangeTracking);
        SetWatermark(task, "40");

        // A polling sequence crossing 40 at exactly one tick. 12:20 is the first check that saw a
        // version at or above where the mapping now is, so it is the earliest moment we can say the
        // source had got there — and the estimate runs from it to the latest check, 40 minutes on.
        RecordCheck(database, MsSqlDriverKinds.ChangeTracking, "10", Origin);
        RecordCheck(database, MsSqlDriverKinds.ChangeTracking, "25", Origin.AddMinutes(10));
        RecordCheck(database, MsSqlDriverKinds.ChangeTracking, "55", Origin.AddMinutes(20));
        RecordCheck(database, MsSqlDriverKinds.ChangeTracking, "80", Origin.AddMinutes(30));
        RecordCheck(database, MsSqlDriverKinds.ChangeTracking, "100", Origin.AddMinutes(60));

        var result = BuildLag().Describe(task, "orders");

        Assert.Equal(TimeSpan.FromMinutes(40), result.EstimatedLag);
        Assert.Equal(60, result.ExactVersionsBehind);
        Assert.Null(result.ExactLag);
    }

    [Fact]
    public async Task ChangeTrackingEstimated_ComparesVersionsAsNumbersRatherThanAsText()
    {
        var (task, database) = await SetUpAsync(MsSqlDriverKinds.ChangeTracking);
        SetWatermark(task, "10");

        // The pitfall this store's three engines all share. Ordered as text, "9" is greater than "10"
        // and the scan would anchor on the 12:00 row, reporting the mapping as 30 minutes behind when
        // it is 10. Small numbers because that is where a text comparison goes wrong first, and where
        // a fresh install lives.
        RecordCheck(database, MsSqlDriverKinds.ChangeTracking, "9", Origin);
        RecordCheck(database, MsSqlDriverKinds.ChangeTracking, "10", Origin.AddMinutes(20));
        RecordCheck(database, MsSqlDriverKinds.ChangeTracking, "12", Origin.AddMinutes(30));

        var result = BuildLag().Describe(task, "orders");

        Assert.Equal(TimeSpan.FromMinutes(10), result.EstimatedLag);
        Assert.Equal(2, result.ExactVersionsBehind);
    }

    [Fact]
    public async Task ChangeTrackingEstimated_IsZeroForACaughtUpMapping()
    {
        var (task, database) = await SetUpAsync(MsSqlDriverKinds.ChangeTracking);
        SetWatermark(task, "100");

        RecordCheck(database, MsSqlDriverKinds.ChangeTracking, "60", Origin);
        RecordCheck(database, MsSqlDriverKinds.ChangeTracking, "100", Origin.AddMinutes(30));

        var result = BuildLag().Describe(task, "orders");

        // The mapping's crossing row is the latest row, so the two ends of the subtraction are the
        // same instant. Zero — and it stays zero however long the source then stays quiet.
        Assert.Equal(TimeSpan.Zero, result.EstimatedLag);
        Assert.Equal(0, result.ExactVersionsBehind);
    }

    [Fact]
    public async Task ChangeTrackingEstimated_IsNullWhenHistoryNeverReachesTheMappingsVersion()
    {
        var (task, database) = await SetUpAsync(MsSqlDriverKinds.ChangeTracking);
        SetWatermark(task, "500");

        RecordCheck(database, MsSqlDriverKinds.ChangeTracking, "60", Origin);
        RecordCheck(database, MsSqlDriverKinds.ChangeTracking, "100", Origin.AddMinutes(30));

        var result = BuildLag().Describe(task, "orders");

        // Nothing anywhere answers "what time did version 500 first exist" — not the history, and
        // since phase 87 not a live query either. Null, never a synthesised zero, which would read as
        // "caught up" — the one thing this mapping is provably not.
        Assert.Null(result.EstimatedLag);

        // And the exact figure is still reported, because it needs no history beyond the latest row.
        Assert.Equal(0, result.ExactVersionsBehind);
    }

    [Fact]
    public async Task ChangeTracking_ReportsNothingWhenItsGroupHasNeverBeenPolled()
    {
        var (task, _) = await SetUpAsync(MsSqlDriverKinds.ChangeTracking);
        SetWatermark(task, "40", Origin);

        var result = BuildLag().Describe(task, "orders");

        Assert.True(result.Supported);
        Assert.Null(result.ExactVersionsBehind);
        Assert.Null(result.EstimatedLag);
        Assert.Null(result.ExactLag);
    }

    // ---- Mechanisms with nothing to say, and the endpoint ---------------------------------------

    [Fact]
    public async Task AReaderWithNoDatabaseWideCounter_SaysSoRatherThanReportingNothing()
    {
        // watermarkColumn because the Watermark reader requires it and the config endpoint is right
        // to reject a task without it. The reader kind under test is one with no database-wide
        // counter, not one with an incomplete configuration — those are different answers and this
        // test wants the first.
        var (task, _) = await SetUpAsync(
            GenericDriverKinds.Watermark, new Dictionary<string, string> { ["watermarkColumn"] = "ModifiedAt" });

        var result = BuildLag().Describe(task, "orders");

        // "Not applicable" and "nothing yet" are different states, and a consumer must be able to
        // tell them apart without inferring it from three nulls.
        Assert.False(result.Supported);
        Assert.Equal(GenericDriverKinds.Watermark, result.ReaderKind);
    }

    [Fact]
    public async Task TheEndpointReportsAnExactFigureWithoutEverReachingTheSource()
    {
        var (task, database) = await SetUpAsync(MsSqlDriverKinds.Cdc);
        SetWatermark(task, Lsn(0, 0, 0, 42, 0, 0, 0, 100, 0, 1), Origin - TimeSpan.FromMinutes(7));
        RecordCheck(database, MsSqlDriverKinds.Cdc, Lsn(0, 0, 0, 42, 0, 0, 0, 200, 0, 1),
            Origin, sourceTime: Origin);

        // Over HTTP, through the API's real DI — which resolves the real DriverChangeCounterSource
        // and a 'lag-src' connection pointing at a SQL Server that is not there. Under phase 85 this
        // exact request had to call fn_cdc_map_lsn_to_time to place the mapping's own position, so it
        // could only have come back null. An exact seven minutes is the regression test for the whole
        // phase: the figure is complete and no source was involved in producing it.
        var response = await _client.GetAsync(
            $"/api/replications/{task.Name}/table-mappings/orders/lag");
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadFromJsonAsync<MappingLag>(JsonOptions);

        Assert.NotNull(body);
        Assert.True(body.Supported);
        Assert.Equal(420_000, body.ExactLagMs);
    }

    [Fact]
    public async Task TheEndpointReportsTheThreeFiguresUnderThreeSeparateNames()
    {
        var (task, database) = await SetUpAsync(MsSqlDriverKinds.ChangeTracking);
        SetWatermark(task, "40");
        RecordCheck(database, MsSqlDriverKinds.ChangeTracking, "40", Origin);
        RecordCheck(database, MsSqlDriverKinds.ChangeTracking, "100", Origin.AddMinutes(15));

        var response = await _client.GetAsync(
            $"/api/replications/{task.Name}/table-mappings/orders/lag");
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadFromJsonAsync<MappingLag>(JsonOptions);

        Assert.NotNull(body);
        Assert.True(body.Supported);
        Assert.Equal(MsSqlDriverKinds.ChangeTracking, body.ReaderKind);
        Assert.Equal(60, body.VersionsBehind);
        Assert.Equal(900_000, body.EstimatedLagMs);

        // The distinction the shape exists to keep: an estimate reconstructed from poll times never
        // arrives in the field a consumer would chart CDC's engine-stated duration from.
        Assert.Null(body.ExactLagMs);
    }

    /// <summary>
    /// Unchanged by phase 87, and worth keeping said: dropping the async from the action did not
    /// turn an unknown mapping into an empty 200 that a screen would render as "no lag data".
    /// </summary>
    [Fact]
    public async Task TheEndpointIsNotFoundForAMappingThatDoesNotExist()
    {
        var (task, _) = await SetUpAsync(MsSqlDriverKinds.ChangeTracking);

        var response = await _client.GetAsync(
            $"/api/replications/{task.Name}/table-mappings/nosuchmapping/lag");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ---- The "as of" time (phase 88) ------------------------------------------------------------
    //
    // The one thing every one of these asserts is that the timestamp names the *same row* the figure
    // beside it came from. That is the whole requirement: an "as of" taken from the group's newest
    // row would look right in every ordinary case and be wrong in exactly the cases somebody is
    // reading this screen for — a capture job that has stopped, a mechanism whose commit-time
    // mapping has aged out — where the figure and the row it rests on deliberately part company
    // with the newest reading.

    [Fact]
    public async Task AsOf_ForCdc_IsTheRowTheFigureUsed_NotTheGroupsNewestRow()
    {
        var (task, database) = await SetUpAsync(MsSqlDriverKinds.Cdc);
        SetWatermark(task, Lsn(0, 0, 0, 42, 0, 0, 0, 100, 0, 1), Origin - TimeSpan.FromMinutes(7));

        RecordCheck(database, MsSqlDriverKinds.Cdc, Lsn(0, 0, 0, 42, 0, 0, 0, 200, 0, 1),
            Origin, sourceTime: Origin);

        // A newer poll that found no position at all — the capture job stopped five minutes ago.
        // CDC's figure deliberately measures against the last row that *has* a time rather than the
        // last row, so the lag keeps growing for exactly the reason it should; the "as of" has to
        // skip the same row, or it would claim a reading that produced no figure.
        RecordCheck(database, MsSqlDriverKinds.Cdc, null, Origin.AddMinutes(5));

        var result = BuildLag().Describe(task, "orders");

        Assert.Equal(TimeSpan.FromMinutes(7), result.ExactLag);
        Assert.Equal(Origin, result.AsOfUtc);
    }

    [Fact]
    public async Task AsOf_ForAnExactChangeTrackingFigure_IsTheEnginesCommitTimeNotThePollTime()
    {
        var (task, database) = await SetUpAsync(MsSqlDriverKinds.ChangeTracking);
        SetWatermark(task, "40", Origin.AddMinutes(1));

        // Polled at 12:00; the engine says the version it found committed at 12:21. The exact figure
        // is the difference between two engine-stated times, so the instant it is measured against
        // is the engine's — reporting 12:00 beside a figure computed from 12:21 would describe a
        // subtraction nobody performed.
        RecordCheck(database, MsSqlDriverKinds.ChangeTracking, "100", Origin,
            sourceTime: Origin.AddMinutes(21));

        var result = BuildLag().Describe(task, "orders");

        Assert.Equal(TimeSpan.FromMinutes(20), result.ExactLag);
        Assert.Equal(Origin.AddMinutes(21), result.AsOfUtc);
    }

    [Fact]
    public async Task AsOf_ForAChangeTrackingEstimate_IsThePollTimeTheEstimateRanTo()
    {
        var (task, database) = await SetUpAsync(MsSqlDriverKinds.ChangeTracking);
        SetWatermark(task, "40");

        RecordCheck(database, MsSqlDriverKinds.ChangeTracking, "10", Origin);
        RecordCheck(database, MsSqlDriverKinds.ChangeTracking, "55", Origin.AddMinutes(20));
        RecordCheck(database, MsSqlDriverKinds.ChangeTracking, "100", Origin.AddMinutes(60));

        var result = BuildLag().Describe(task, "orders");

        // The estimate runs from the crossing poll to the latest poll, both wall-clock instants this
        // system chose — so the "as of" is that same latest poll. It follows the figure between the
        // two fields rather than being fixed to either column.
        Assert.Equal(TimeSpan.FromMinutes(40), result.EstimatedLag);
        Assert.Equal(Origin.AddMinutes(60), result.AsOfUtc);
    }

    [Fact]
    public async Task AsOf_IsReportedEvenWhereTheFigureIsNot()
    {
        var (task, database) = await SetUpAsync(MsSqlDriverKinds.Cdc);

        // A mapping that has run — so it has a position — but whose pass never captured a time for
        // it, which is every CDC mapping that last ran before phase 87 shipped.
        SetWatermark(task, Lsn(0, 0, 0, 42, 0, 0, 0, 100, 0, 1));
        RecordCheck(database, MsSqlDriverKinds.Cdc, Lsn(0, 0, 0, 42, 0, 0, 0, 200, 0, 1),
            Origin, sourceTime: Origin);

        var result = BuildLag().Describe(task, "orders");

        // No figure, and still an answer to "when did we last see the source". The two absences a
        // blank cell used to conflate — a poller that has stopped, and a mapping that cannot be
        // placed — are different problems, and only the first is about this system.
        Assert.Null(result.ExactLag);
        Assert.Equal(Origin, result.AsOfUtc);
    }

    [Fact]
    public async Task AsOf_IsNullWhereNoReadingWasConsultedAtAll()
    {
        var (task, database) = await SetUpAsync(
            GenericDriverKinds.Watermark,
            new Dictionary<string, string> { ["watermarkColumn"] = "ModifiedAt" });
        RecordCheck(database, MsSqlDriverKinds.ChangeTracking, "100", Origin, sourceTime: Origin);

        // A reader with no database-wide position is in no polling group, so there is no reading
        // behind it — not even a stale one. Null rather than the newest row of some group it does
        // not belong to.
        Assert.Null(BuildLag().Describe(task, "orders").AsOfUtc);
    }
}
