using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using DataSync.Api.Controllers;
using DataSync.Api.Services;
using DataSync.Core.Config;
using DataSync.Core.Sql;
using DataSync.Drivers.Generic;
using DataSync.Drivers.MsSql;
using DataSync.State;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DataSync.Api.Tests;

/// <summary>
/// Reader lag — see phase 85. Three figures over two mechanisms: CDC's real duration from the
/// engine's own LSN-to-time mapping, Change Tracking's exact version count, and Change Tracking's
/// duration — exact too while <c>dm_tran_commit_table</c> can still place its versions, and
/// reconstructed from this system's own polling history once it cannot.
/// <para>
/// **A test that leaves a version out of the fake's <c>Times</c> is choosing the fallback path**, the
/// way a real DMV chooses it by ageing the version out. The pair of paths is asserted in both
/// directions — that an available exact answer is taken and leaves the estimate null, and that an
/// unavailable one produces an estimate and leaves the exact field null — because a shape whose whole
/// purpose is to keep two precisions apart is only doing that if neither can be reached through the
/// other's field.
/// </para>
/// <para>
/// **What every one of these is really asserting is that nothing here is measured against now.** The
/// definition these replace — now, minus the time of the last change we applied — is correct on a
/// busy source and wrong in the only case that matters: on a quiet, fully caught-up replication it
/// climbs for ever, and an alarm built on it goes off when there is nothing to do. Each figure below
/// has the source's own last-known position on the other side of the subtraction instead, which is
/// why <c>OnAQuietCaughtUpSource</c> can assert zero against history written an hour ago.
/// </para>
/// <para>
/// The counter round-trip is the one thing faked, as it is for the gate, and for the same reason.
/// The config, the driver registry that names the dialect the watermark key is spelled in, and both
/// state tables are the real ones the API is wired with.
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

    private (ReaderLagService Lag, FakeChangeCounterSource Source) BuildLag()
    {
        var source = new FakeChangeCounterSource();
        var lag = new ReaderLagService(
            factory.Services.GetRequiredService<ChangeSourceResolver>(),
            source,
            factory.Services.GetRequiredService<ChangeWatermarkStore>(),
            factory.Services.GetRequiredService<ChangeCheckStore>(),
            NullLogger<ReaderLagService>.Instance);
        return (lag, source);
    }

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
            Password = "DataSync_Test_Pw1",
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

    /// <summary>Writes the mapping's watermark under the key the lag service will read it by — built
    /// through the source's own dialect, so a change to WatermarkKey.Build cannot leave these passing
    /// against a key nothing else uses.</summary>
    private void SetWatermark(ReplicationTaskConfig task, string watermark)
    {
        var source = EndpointResolution.ResolveSource(
            task, new SourceTableSpec { Schema = "dbo", Table = "orders" });
        factory.Services.GetRequiredService<ChangeWatermarkStore>().SetWatermark(
            task.Name, "orders", WatermarkKey.Build(source, MsSqlDialect.Instance), watermark);
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
        var applied = Lsn(0, 0, 0, 42, 0, 0, 0, 100, 0, 1);
        SetWatermark(task, applied);

        // The source had reached 12:00 when it was last polled; the mapping's own position maps to
        // 11:55. Five minutes behind, and both ends come from the engine's own mapping function.
        RecordCheck(database, MsSqlDriverKinds.Cdc, Lsn(0, 0, 0, 42, 0, 0, 0, 200, 0, 1),
            Origin, sourceTime: Origin);

        var (lag, source) = BuildLag();
        source.Times[applied] = Origin - TimeSpan.FromMinutes(5);

        var result = await lag.DescribeAsync(task, "orders", CancellationToken.None);

        Assert.True(result.Supported);
        Assert.Equal(TimeSpan.FromMinutes(5), result.ExactLag);
        Assert.Null(result.ExactVersionsBehind);
        Assert.Null(result.EstimatedLag);

        // Read out of the history the gate already wrote. A status screen refreshing every few
        // seconds must not become a second poller of the source.
        Assert.Empty(source.Fetches);
    }

    [Fact]
    public async Task CdcLag_OnAQuietCaughtUpSource_DoesNotGrowWithTheWallClock()
    {
        var (task, database) = await SetUpAsync(MsSqlDriverKinds.Cdc);
        var applied = Lsn(0, 0, 0, 42, 0, 0, 0, 200, 0, 1);
        SetWatermark(task, applied);

        // The bug the naive definition would have shipped, written as a test: the last poll was an
        // hour ago and the source has not moved since. Against wall-clock now this reads as an hour
        // behind. Against the source's own position it is what it is — caught up.
        var lastPoll = DateTimeOffset.UtcNow - TimeSpan.FromHours(1);
        RecordCheck(database, MsSqlDriverKinds.Cdc, applied, lastPoll, sourceTime: lastPoll);

        var (lag, source) = BuildLag();
        source.Times[applied] = lastPoll;

        var result = await lag.DescribeAsync(task, "orders", CancellationToken.None);

        Assert.Equal(TimeSpan.Zero, result.ExactLag);
    }

    [Fact]
    public async Task CdcLag_ClampsAMappingAheadOfTheLastPollToZero()
    {
        var (task, database) = await SetUpAsync(MsSqlDriverKinds.Cdc);
        var applied = Lsn(0, 0, 0, 42, 0, 0, 1, 44, 0, 1);
        SetWatermark(task, applied);
        RecordCheck(database, MsSqlDriverKinds.Cdc, Lsn(0, 0, 0, 42, 0, 0, 0, 100, 0, 1),
            Origin, sourceTime: Origin);

        var (lag, source) = BuildLag();

        // Legitimate, not a corruption: a pass ran between two polls and read past the position the
        // earlier one recorded. Negative is not a lag, and reporting one would be a UI showing a
        // replication as "-90s behind".
        source.Times[applied] = Origin + TimeSpan.FromMinutes(90);

        var result = await lag.DescribeAsync(task, "orders", CancellationToken.None);

        Assert.Equal(TimeSpan.Zero, result.ExactLag);
    }

    [Fact]
    public async Task CdcLag_FallsBackToALiveFetchWhenNoHistoryCarriesASourceTime()
    {
        var (task, database) = await SetUpAsync(MsSqlDriverKinds.Cdc);
        var applied = Lsn(0, 0, 0, 42, 0, 0, 0, 100, 0, 1);
        SetWatermark(task, applied);

        // A row exists, but from before the capture job had produced a position at all — so it has no
        // time on it, and the group has nothing usable to measure against. The fresh install case.
        RecordCheck(database, MsSqlDriverKinds.Cdc, value: null, Origin);

        var (lag, source) = BuildLag();
        source.Set("lag-src", database, MsSqlDriverKinds.Cdc,
            Lsn(0, 0, 0, 42, 0, 0, 0, 200, 0, 1), sourceTime: Origin);
        source.Times[applied] = Origin - TimeSpan.FromMinutes(2);

        var result = await lag.DescribeAsync(task, "orders", CancellationToken.None);

        Assert.Equal(TimeSpan.FromMinutes(2), result.ExactLag);
        Assert.Single(source.Fetches);
    }

    [Fact]
    public async Task CdcLag_IsNullBeforeTheMappingHasEverRead()
    {
        var (task, database) = await SetUpAsync(MsSqlDriverKinds.Cdc);
        RecordCheck(database, MsSqlDriverKinds.Cdc, Lsn(0, 0, 0, 42, 0, 0, 0, 200, 0, 1),
            Origin, sourceTime: Origin);

        var (lag, _) = BuildLag();

        // Supported, and unknown. A first pass has no position to be behind from, and a big number
        // there would read as an alarm about a replication that has simply not started.
        var result = await lag.DescribeAsync(task, "orders", CancellationToken.None);

        Assert.True(result.Supported);
        Assert.Null(result.ExactLag);
    }

    [Fact]
    public async Task CdcLag_IsNullWhenTheMappingsPositionHasAgedOutOfTheMapping()
    {
        var (task, database) = await SetUpAsync(MsSqlDriverKinds.Cdc);
        SetWatermark(task, Lsn(0, 0, 0, 1, 0, 0, 0, 1, 0, 1));
        RecordCheck(database, MsSqlDriverKinds.Cdc, Lsn(0, 0, 0, 42, 0, 0, 0, 200, 0, 1),
            Origin, sourceTime: Origin);

        // fn_cdc_map_lsn_to_time answers null for a position outside the retained window. Null out,
        // rather than a fabricated distance from a time the source declined to state.
        var (lag, _) = BuildLag();

        var result = await lag.DescribeAsync(task, "orders", CancellationToken.None);

        Assert.Null(result.ExactLag);
    }

    [Fact]
    public async Task CdcLag_IsNullRatherThanAnErrorWhenTheSourceCannotBeReached()
    {
        var (task, database) = await SetUpAsync(MsSqlDriverKinds.Cdc);
        SetWatermark(task, Lsn(0, 0, 0, 42, 0, 0, 0, 100, 0, 1));

        var (lag, source) = BuildLag();
        source.Unreachable.Add(("lag-src", database, MsSqlDriverKinds.Cdc));

        // Asking how far behind something is must never be able to fail the screen it is on.
        var result = await lag.DescribeAsync(task, "orders", CancellationToken.None);

        Assert.True(result.Supported);
        Assert.Null(result.ExactLag);
    }

    // ---- Change Tracking, exact -----------------------------------------------------------------

    [Fact]
    public async Task ChangeTrackingExact_IsTheVersionDifference_WithNoRoundTrip()
    {
        var (task, database) = await SetUpAsync(MsSqlDriverKinds.ChangeTracking);
        SetWatermark(task, "40");
        RecordCheck(database, MsSqlDriverKinds.ChangeTracking, "100", Origin);

        var (lag, source) = BuildLag();

        var result = await lag.DescribeAsync(task, "orders", CancellationToken.None);

        // Sixty versions, exact by construction: both numbers were already stored, neither is
        // estimated, and no time is claimed about them — what a version is worth depends entirely on
        // how often the source's tables are written to.
        Assert.Equal(60, result.ExactVersionsBehind);
        Assert.Null(result.ExactLag);
        Assert.Empty(source.Fetches);
    }

    [Fact]
    public async Task ChangeTrackingExact_ClampsAMappingAheadOfTheLastPollToZero()
    {
        var (task, database) = await SetUpAsync(MsSqlDriverKinds.ChangeTracking);
        SetWatermark(task, "140");
        RecordCheck(database, MsSqlDriverKinds.ChangeTracking, "100", Origin);

        var (lag, _) = BuildLag();

        var result = await lag.DescribeAsync(task, "orders", CancellationToken.None);

        Assert.Equal(0, result.ExactVersionsBehind);
    }

    // ---- Change Tracking, exact time via dm_tran_commit_table -----------------------------------

    [Fact]
    public async Task ChangeTrackingTime_IsExactWhileTheEngineStillHoldsBothVersions()
    {
        var (task, database) = await SetUpAsync(MsSqlDriverKinds.ChangeTracking);
        SetWatermark(task, "40");

        // A polling sequence that would produce a 40-minute *estimate* if it were consulted. It is
        // not, because the DMV can still place both versions, and 25 minutes is what actually
        // elapsed between the two commits.
        RecordCheck(database, MsSqlDriverKinds.ChangeTracking, "10", Origin);
        RecordCheck(database, MsSqlDriverKinds.ChangeTracking, "100", Origin.AddMinutes(40));

        var (lag, source) = BuildLag();
        source.Times["40"] = Origin.AddMinutes(5);
        source.Times["100"] = Origin.AddMinutes(30);

        var result = await lag.DescribeAsync(task, "orders", CancellationToken.None);

        // Exact, in the same field CDC's exact figure arrives in, because it is exact in the same
        // sense: both ends are times the engine itself stated for those versions.
        Assert.Equal(TimeSpan.FromMinutes(25), result.ExactLag);
        Assert.Equal(60, result.ExactVersionsBehind);

        // And the estimate is absent, not merely different. A consumer must not have to know which
        // of two populated fields is the better one.
        Assert.Null(result.EstimatedLag);
    }

    [Fact]
    public async Task ChangeTrackingTime_FallsBackToTheEstimateOnlyOnceTheVersionHasAgedOut()
    {
        var (task, database) = await SetUpAsync(MsSqlDriverKinds.ChangeTracking);
        SetWatermark(task, "40");

        RecordCheck(database, MsSqlDriverKinds.ChangeTracking, "25", Origin);
        RecordCheck(database, MsSqlDriverKinds.ChangeTracking, "55", Origin.AddMinutes(20));
        RecordCheck(database, MsSqlDriverKinds.ChangeTracking, "100", Origin.AddMinutes(60));

        var (lag, source) = BuildLag();

        // The current version is still in the DMV's window; the mapping's own, being older, is not.
        // This is the ordinary shape of a mapping far enough behind to be worth reporting on, and
        // it is the condition — the *only* condition — that hands the figure to the estimate.
        source.Times["100"] = Origin.AddMinutes(60);

        var result = await lag.DescribeAsync(task, "orders", CancellationToken.None);

        Assert.Null(result.ExactLag);
        Assert.Equal(TimeSpan.FromMinutes(40), result.EstimatedLag);
    }

    [Fact]
    public async Task ChangeTrackingTime_NeverMixesAnEngineTimeWithAPollTime()
    {
        var (task, database) = await SetUpAsync(MsSqlDriverKinds.ChangeTracking);
        SetWatermark(task, "40");

        RecordCheck(database, MsSqlDriverKinds.ChangeTracking, "40", Origin);
        RecordCheck(database, MsSqlDriverKinds.ChangeTracking, "100", Origin.AddMinutes(50));

        var (lag, source) = BuildLag();

        // The unlikely half of the window: the mapping's version is placeable and the group's
        // current one is not. Half an exact answer is available, and taking it would mean
        // subtracting an engine-stated commit time from the wall-clock instant a poll happened to
        // run — a figure whose error is the polling interval, reported in the field that promises
        // there is none. Both ends from the engine, or the estimate, which at least owns its error.
        source.Times["40"] = Origin.AddMinutes(10);

        var result = await lag.DescribeAsync(task, "orders", CancellationToken.None);

        Assert.Null(result.ExactLag);
        Assert.Equal(TimeSpan.FromMinutes(50), result.EstimatedLag);
    }

    [Fact]
    public async Task ChangeTrackingTime_FallsBackToTheEstimateWhenTheSourceCannotBeReached()
    {
        var (task, database) = await SetUpAsync(MsSqlDriverKinds.ChangeTracking);
        SetWatermark(task, "40");

        RecordCheck(database, MsSqlDriverKinds.ChangeTracking, "40", Origin);
        RecordCheck(database, MsSqlDriverKinds.ChangeTracking, "100", Origin.AddMinutes(15));

        var (lag, source) = BuildLag();
        source.Unreachable.Add(("lag-src", database, MsSqlDriverKinds.ChangeTracking));

        // Unlike CDC, there is somewhere to go when the source will not answer: the estimate needs
        // no source at all. An unreachable server costs this figure its precision, not its
        // existence.
        var result = await lag.DescribeAsync(task, "orders", CancellationToken.None);

        Assert.Null(result.ExactLag);
        Assert.Equal(TimeSpan.FromMinutes(15), result.EstimatedLag);
        Assert.Equal(60, result.ExactVersionsBehind);
    }

    [Fact]
    public async Task TheEndpointReportsAnExactChangeTrackingTimeInTheExactField()
    {
        var (task, database) = await SetUpAsync(MsSqlDriverKinds.ChangeTracking);
        SetWatermark(task, "40");
        RecordCheck(database, MsSqlDriverKinds.ChangeTracking, "100", Origin);

        // Registered directly rather than through the API's own DI, because the endpoint resolves
        // the real DriverChangeCounterSource and this test is about which field the figure lands in
        // over the wire, not about reaching a server.
        var (lag, source) = BuildLag();
        source.Times["40"] = Origin.AddMinutes(1);
        source.Times["100"] = Origin.AddMinutes(21);

        var result = await lag.DescribeAsync(task, "orders", CancellationToken.None);
        var body = MappingLag.From(result);

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

        var (lag, source) = BuildLag();

        var result = await lag.DescribeAsync(task, "orders", CancellationToken.None);

        Assert.Equal(TimeSpan.FromMinutes(40), result.EstimatedLag);
        Assert.Equal(60, result.ExactVersionsBehind);
        Assert.Null(result.ExactLag);
        Assert.Empty(source.Fetches);
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

        var (lag, _) = BuildLag();

        var result = await lag.DescribeAsync(task, "orders", CancellationToken.None);

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

        var (lag, _) = BuildLag();

        var result = await lag.DescribeAsync(task, "orders", CancellationToken.None);

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

        var (lag, _) = BuildLag();

        var result = await lag.DescribeAsync(task, "orders", CancellationToken.None);

        // No live fallback exists for this figure and none can: nothing on the source answers "what
        // time did version 500 first exist". Null, never a synthesised zero, which would read as
        // "caught up" — the one thing this mapping is provably not.
        Assert.Null(result.EstimatedLag);

        // And the exact figure is still reported, because it needs no history beyond the latest row.
        Assert.Equal(0, result.ExactVersionsBehind);
    }

    [Fact]
    public async Task ChangeTracking_ReportsNothingWhenItsGroupHasNeverBeenPolled()
    {
        var (task, _) = await SetUpAsync(MsSqlDriverKinds.ChangeTracking);
        SetWatermark(task, "40");

        var (lag, _) = BuildLag();

        var result = await lag.DescribeAsync(task, "orders", CancellationToken.None);

        Assert.True(result.Supported);
        Assert.Null(result.ExactVersionsBehind);
        Assert.Null(result.EstimatedLag);
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

        var (lag, _) = BuildLag();

        var result = await lag.DescribeAsync(task, "orders", CancellationToken.None);

        // "Not applicable" and "nothing yet" are different states, and a consumer must be able to
        // tell them apart without inferring it from three nulls.
        Assert.False(result.Supported);
        Assert.Equal(GenericDriverKinds.Watermark, result.ReaderKind);
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

    [Fact]
    public async Task TheEndpointIsNotFoundForAMappingThatDoesNotExist()
    {
        var (task, _) = await SetUpAsync(MsSqlDriverKinds.ChangeTracking);

        var response = await _client.GetAsync(
            $"/api/replications/{task.Name}/table-mappings/nosuchmapping/lag");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
