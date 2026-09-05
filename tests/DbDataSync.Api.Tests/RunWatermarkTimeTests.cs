using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using DbDataSync.Api.Services;
using DbDataSync.Core.Config;
using DbDataSync.Drivers.Generic;
using DbDataSync.Drivers.MsSql;
using DbDataSync.State;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DbDataSync.Api.Tests;

/// <summary>
/// Dating a run's stored watermarks out of polling history — phase 88.
/// <para>
/// **The table under test is <c>ChangeCheckHistory</c>, and which table it is *is* the phase.** A
/// run's <c>PreviousWatermark</c>/<c>NewWatermark</c> are source positions — an LSN, a version — and
/// turning one into a time means finding a moment the source was observed there. The obvious place
/// to look is <c>ChangeWatermarks</c>, and it cannot answer: one row per mapping, overwritten, so it
/// matches the most recent run and nothing before it. The polling history is the table that spans
/// time, and every test here is a claim about reading a sequence of its rows correctly.
/// </para>
/// <para>
/// **The interesting cases are all the ones with no answer.** A position below the oldest retained
/// row, a position past the newest, a run that never made one durable — each is a different reason
/// for a blank, and none of them may come back as a plausible-looking timestamp. A run list showing
/// a fabricated time is worse than one showing a dash, because only one of the two is arguable.
/// </para>
/// </summary>
public sealed class RunWatermarkTimeTests(TestApiFactory factory) : IClassFixture<TestApiFactory>
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly HttpClient _client = factory.CreateClient();

    /// <summary>A fixed instant to hang every history sequence off, so a test states the distances it
    /// asserts rather than the clock it happened to run on.</summary>
    private static readonly DateTimeOffset Origin = new(2026, 5, 1, 9, 0, 0, TimeSpan.Zero);

    // ---- The lookup -----------------------------------------------------------------------------

    [Fact]
    public async Task AWatermark_IsDatedByTheFirstPollThatObservedTheSourceAtOrPastIt()
    {
        var (task, database) = await SetUpAsync();

        // A source climbing 10 → 50 → 100. The 50 row carries the engine's own commit time for that
        // version; the 100 row does not, which is the state every Change Tracking row was in before
        // phase 87 and any row whose capture failed is in now.
        RecordCheck(database, "10", Origin);
        RecordCheck(database, "50", Origin.AddMinutes(10), sourceTime: Origin.AddMinutes(9));
        RecordCheck(database, "100", Origin.AddMinutes(20));

        // A pass that advanced the mapping from version 20 to version 60. Neither number appears in
        // the history — which is the ordinary case, since the gate polls on its own schedule and a
        // pass reads whatever the source had reached in between.
        var runId = CompleteRun(task.Name, "20", "60");

        var times = await GetAsync(task.Name);

        // 20 was first observed reached by the 50 row, and that row states when the source says
        // version 50 committed: 9:09, not the 9:10 we happened to ask at.
        Assert.Equal(Origin.AddMinutes(9), times[runId].PreviousWatermarkTimeUtc);

        // 60 was first observed reached by the 100 row, which has no engine time — so the fallback
        // is when we asked. A real answer with a poll-interval's error, rather than no answer.
        Assert.Equal(Origin.AddMinutes(20), times[runId].NewWatermarkTimeUtc);
    }

    [Fact]
    public async Task AWatermarkOlderThanTheRetainedHistory_HasNoTimestampRatherThanAFabricatedOne()
    {
        var (task, database) = await SetUpAsync();

        // The oldest row this group still has is already at version 500 — everything before it has
        // been purged by the retention window.
        RecordCheck(database, "500", Origin);
        RecordCheck(database, "600", Origin.AddMinutes(10));

        // A run from before that window. Its previous watermark is under the oldest retained value
        // and its new one is inside the retained band.
        var runId = CompleteRun(task.Name, "7", "550");

        var times = await GetAsync(task.Name);

        // **The trap this test exists for.** Counter values only climb, so "the earliest row that
        // reached 7" matches the *oldest retained row* — and reporting its timestamp would date a
        // months-old run to whenever the purge last ran. Every run older than the retention window
        // would come back stamped with the same recent time, which is the most confident-looking
        // way to be wrong available.
        Assert.Null(times[runId].PreviousWatermarkTimeUtc);

        // The half that can be answered still is. A pass that started outside the window and ended
        // inside it gets the end, rather than being withheld for the sake of consistency.
        Assert.Equal(Origin.AddMinutes(10), times[runId].NewWatermarkTimeUtc);
    }

    [Fact]
    public async Task AWatermarkTheSourceHasNotBeenObservedAtYet_HasNoTimestamp()
    {
        var (task, database) = await SetUpAsync();

        RecordCheck(database, "10", Origin);
        RecordCheck(database, "50", Origin.AddMinutes(10));

        // A pass that read past the last position the gate wrote down — which happens routinely, on
        // any pass that runs between two ticks. Nothing has observed the source at 80, so there is
        // no moment to report.
        var runId = CompleteRun(task.Name, "20", "80");

        var times = await GetAsync(task.Name);

        Assert.Equal(Origin.AddMinutes(10), times[runId].PreviousWatermarkTimeUtc);
        Assert.Null(times[runId].NewWatermarkTimeUtc);
    }

    [Fact]
    public async Task ARunThatMadeNoPositionDurable_IsAbsentFromTheMap()
    {
        var (task, database) = await SetUpAsync();
        RecordCheck(database, "100", Origin);

        // A failed pass, or a backfill, or a verification: all three store null for both watermarks
        // because none of them committed a new position. There is nothing to date, and an entry
        // holding two nulls would say the same thing at more length.
        var runId = CompleteRun(task.Name, null, null, RunStatus.Failed);

        var times = await GetAsync(task.Name);

        Assert.DoesNotContain(runId, times.Keys);
    }

    [Fact]
    public async Task ARunOfAMappingWhoseReaderHasNoSourcePosition_IsAbsentFromTheMap()
    {
        var (task, database) = await SetUpAsync();
        RecordCheck(database, "100", Origin);

        // 'audit' overrides to a reader with no database-wide counter — it is in no polling group,
        // so there is no history to look its positions up in. Absent rather than an error, the same
        // judgement the lag service makes about the same mapping.
        var runId = CompleteRun(task.Name, "20", "60", mappingName: "audit");

        var times = await GetAsync(task.Name);

        Assert.DoesNotContain(runId, times.Keys);
    }

    [Fact]
    public async Task EveryRunOnThePageIsDatedFromOneReadOfTheGroupsHistory()
    {
        var (task, database) = await SetUpAsync();

        RecordCheck(database, "10", Origin);
        RecordCheck(database, "50", Origin.AddMinutes(10));
        RecordCheck(database, "100", Origin.AddMinutes(20));
        RecordCheck(database, "200", Origin.AddMinutes(30));

        // Three consecutive passes of the same mapping, which is what a run list actually holds —
        // and which shares watermarks between rows, since each pass starts where the last stopped.
        var first = CompleteRun(task.Name, "20", "60");
        var second = CompleteRun(task.Name, "60", "120");
        // The newest pass has read past the last position the gate wrote down, which is the ordinary
        // state of the top row of a run list: the pass ran between two ticks.
        var third = CompleteRun(task.Name, "120", "250");

        var times = await GetAsync(task.Name);

        // The property under test is that a page resolves as a page: six watermark lookups across
        // three runs, four distinct values between them, one pass over the history. What is
        // assertable from out here is that the batching did not lose or cross any of them — every
        // run keeps its own pair, the shared boundary is dated identically on both sides of it, and
        // the one target the history cannot place is the only one left blank.
        Assert.Equal(Origin.AddMinutes(10), times[first].PreviousWatermarkTimeUtc);
        Assert.Equal(Origin.AddMinutes(20), times[first].NewWatermarkTimeUtc);
        Assert.Equal(Origin.AddMinutes(20), times[second].PreviousWatermarkTimeUtc);
        Assert.Equal(Origin.AddMinutes(30), times[second].NewWatermarkTimeUtc);
        Assert.Equal(Origin.AddMinutes(30), times[third].PreviousWatermarkTimeUtc);
        Assert.Null(times[third].NewWatermarkTimeUtc);
    }

    [Fact]
    public async Task TheEndpointIsNotFoundForAReplicationThatDoesNotExist()
    {
        var response = await _client.GetAsync(
            $"/api/replications/nosuch-{Guid.NewGuid():N}/runs/watermark-times");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    /// <summary>
    /// Phase 104's own reason for touching this endpoint: it has to take the *same* filters as
    /// history and resolve the *same* page, not a superset or a subset of it.
    /// <para>
    /// Two runs a <c>mappingName=orders&amp;status=Succeeded</c> filter keeps, and one it excludes —
    /// deliberately given a watermark just as resolvable as the kept two's, so a bug that had this
    /// endpoint ignore the filters entirely would still pass a test whose excluded run had nothing to
    /// date.
    /// </para>
    /// </summary>
    [Fact]
    public async Task WatermarkTimes_TakesTheSameFiltersAsHistory_AndAnswersExactlyThatSet()
    {
        var (task, database) = await SetUpAsync();

        RecordCheck(database, "10", Origin);
        RecordCheck(database, "50", Origin.AddMinutes(10));
        RecordCheck(database, "100", Origin.AddMinutes(20));

        var kept1 = CompleteRun(task.Name, "10", "50");
        var kept2 = CompleteRun(task.Name, "50", "100");
        // Same mapping, same resolvable watermarks, wrong status — history's filter has to drop this
        // one, and watermark-times has to agree rather than dating it anyway.
        var excludedByStatus = CompleteRun(task.Name, "10", "50", RunStatus.Failed);

        const string filter = "mappingName=orders&status=Succeeded";

        var historyResponse = await _client.GetAsync($"/api/replications/{task.Name}/runs?{filter}");
        historyResponse.EnsureSuccessStatusCode();
        var history = await historyResponse.Content.ReadFromJsonAsync<RunHistoryResponseDto>(JsonOptions);
        var expectedIds = history!.Runs.Select(r => r.RunId).ToHashSet();
        Assert.Equal(new HashSet<Guid> { kept1, kept2 }, expectedIds);

        var times = await GetAsync(task.Name, filter);

        // Exactly the filtered set: not a superset (the excluded run's watermark leaking in because
        // this endpoint applied no filter of its own) and not a subset (one of the kept two silently
        // missing because the two endpoints resolved different pages).
        Assert.Equal(expectedIds, times.Keys.ToHashSet());
        Assert.DoesNotContain(excludedByStatus, times.Keys);
    }

    /// <summary>Deserialization target for the history endpoint's page shape since phase 104 — mirrors
    /// <c>DbDataSync.Api.Models.RunHistoryResponse</c> field for field.</summary>
    private sealed record RunHistoryResponseDto(List<TaskRunRecord> Runs, string? NextCursor);

    // ---- Fixture --------------------------------------------------------------------------------

    private async Task<Dictionary<Guid, RunWatermarkTimes>> GetAsync(string replicationName, string? query = null)
    {
        var response = await _client.GetAsync(
            $"/api/replications/{replicationName}/runs/watermark-times{(query is null ? "" : $"?{query}")}");
        response.EnsureSuccessStatusCode();

        var body = await response.Content
            .ReadFromJsonAsync<Dictionary<Guid, RunWatermarkTimes>>(JsonOptions);
        Assert.NotNull(body);
        return body;
    }

    /// <summary>
    /// A Change Tracking replication with two mappings: 'orders' inheriting the reader, and 'audit'
    /// overriding it to one with no database-wide position at all.
    /// <para>
    /// The source database is named uniquely per test because the history group is keyed by it and
    /// the fixture's state database is shared — two tests writing checks for one group would read
    /// each other's rows, and the failure would look like a bug in the crossing scan.
    /// </para>
    /// <para>
    /// Change Tracking rather than CDC because its counter is a plain integer, and every assertion
    /// here is about *which* row was picked. A test whose fixture is a sequence of hex LSNs makes
    /// the reader do the ordering in their head before they can see what it claims.
    /// </para>
    /// </summary>
    private async Task<(ReplicationTaskConfig Task, string Database)> SetUpAsync()
    {
        var suffix = Guid.NewGuid().ToString("N")[..12];
        var replicationName = $"wmtime-{suffix}";
        var database = $"App_{suffix}";

        (await _client.PutAsJsonAsync("/api/connections/wmtime-src", new ConnectionInput
        {
            Name = "wmtime-src",
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
                Source = new EndpointRef { ConnectionName = "wmtime-src", Database = database },
                Target = new EndpointRef { ConnectionName = "wmtime-src", Database = "DW" },
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

        // The watermark reader takes a column and a mapping that does not name one is rejected —
        // this fixture wants a reader with no database-wide counter, not one that will not validate.
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

    /// <summary>
    /// One finished run with the watermarks it made durable, through the real queue and the real
    /// stores — the same four calls a TaskRunner makes, so a change to how a run records its
    /// positions cannot leave these tests passing against a row nothing else writes.
    /// <para>
    /// **The claim and the <c>MarkDone</c> are not ceremony.** <c>Enqueue</c> is deduplicated against
    /// a partial unique index over the in-flight statuses, so queueing a second pass of a mapping
    /// whose queue row is still open returns the *first* run's id rather than a new one — correctly,
    /// since that is the whole point of the index. Completing the run alone does not close the queue
    /// row. A helper that skipped this would silently make three consecutive passes into one run
    /// overwritten three times, and the test asserting a page of run history would be asserting
    /// against a page of one.
    /// </para>
    /// </summary>
    private Guid CompleteRun(
        string taskName,
        string? previousWatermark,
        string? newWatermark,
        RunStatus status = RunStatus.Succeeded,
        string mappingName = "orders")
    {
        var queue = factory.Services.GetRequiredService<WorkQueueStore>();
        var runs = factory.Services.GetRequiredService<TaskRunStore>();

        var runId = queue.Enqueue(taskName, RunKind.Primary, mappingName);

        var item = queue.TryClaimNext(taskName, workerId: "watermark-time-tests");
        Assert.NotNull(item);
        Assert.Equal(runId, item.RunId);

        runs.BeginRun(runId, pid: 4242);
        runs.CompleteRun(
            runId, status, rowsRead: 1, rowsWritten: 1, errorSummary: null,
            previousWatermark: previousWatermark, newWatermark: newWatermark);
        queue.MarkDone(item.Id);

        return runId;
    }

    /// <summary>
    /// One history row at a stated time — written straight to the table rather than through
    /// <c>ChangeCheckStore.Record</c>, which stamps <c>UtcNow</c>. Correctly so: a check happens when
    /// it happens, and these tests are about arithmetic over a sequence that needs times a test
    /// chooses.
    /// </summary>
    private void RecordCheck(
        string database, string? value, DateTimeOffset checkedAt, DateTimeOffset? sourceTime = null)
    {
        var db = factory.Services.GetRequiredService<StateDatabase>();
        db.Retry(() =>
        {
            using var connection = db.OpenConnection();
            using var cmd = db.Command(connection, """
                INSERT INTO ChangeCheckHistory (ConnectionName, SourceDatabase, SourceKind, Value, CheckedAtUtc, SourceTimeUtc)
                VALUES ($connection, $database, $kind, $value, $checkedAt, $sourceTime);
                """);
            cmd.Bind(db, "connection", "wmtime-src");
            cmd.Bind(db, "database", database);
            cmd.Bind(db, "kind", MsSqlDriverKinds.ChangeTracking);
            cmd.Bind(db, "value", (object?)value ?? DBNull.Value);
            cmd.Bind(db, "checkedAt", checkedAt.ToString("O"));
            cmd.Bind(db, "sourceTime", (object?)sourceTime?.ToString("O") ?? DBNull.Value);
            cmd.ExecuteNonQuery();
        });
    }
}
