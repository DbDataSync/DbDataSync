using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using DbDataSync.Api.Services;
using DbDataSync.Core.Config;
using DbDataSync.Drivers.Abstractions;
using DbDataSync.Drivers.Generic;
using DbDataSync.State;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DbDataSync.Api.Tests;

/// <summary>
/// Phase 125's scheduled/after-change reconcile trigger, at the same level
/// <see cref="SchedulerServiceHoldTests"/> already exercises the Primary due-ness check at:
/// <see cref="SchedulerService.TickAsync"/> invoked directly by reflection, against real local config
/// and state but no real source database — the mapping's reader is the generic, ungated
/// <c>Watermark</c> Kind, so nothing here ever needs a container. A Primary pass having "read rows" is
/// fabricated directly through <see cref="TaskRunStore"/>, the same way a unit test drives any other
/// store-level scenario, rather than by running a real pass.
/// </summary>
public sealed class SchedulerServiceReconcileTests(TestApiFactory factory) : IClassFixture<TestApiFactory>
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly HttpClient _client = factory.CreateClient();
    private readonly TaskRunStore _taskRunStore = factory.Services.GetRequiredService<TaskRunStore>();
    private readonly WorkQueueStore _workQueueStore = factory.Services.GetRequiredService<WorkQueueStore>();

    private async Task<string> SetUpMappingAsync(
        ReconcileConfig reconcile, IReadOnlyList<BatchReloadSegment>? segments = null,
        [System.Runtime.CompilerServices.CallerMemberName] string testName = "")
    {
        var replicationName = $"rc-sched-{testName}-{Guid.NewGuid():N}";
        const string mappingName = "orders";

        (await _client.PutAsJsonAsync($"/api/connections/rc-src-{testName}", new ConnectionInput
        {
            Name = $"rc-src-{testName}",
            DriverType = DriverIds.MsSql,
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
                Source = new EndpointRef { ConnectionName = $"rc-src-{testName}", Database = "App" },
                Target = new EndpointRef { ConnectionName = $"rc-src-{testName}", Database = "DW" },
            },
            Scheduling = new SchedulingConfig { Mode = ScheduleMode.Continuous, FrequencySeconds = 3600 },
            ChangeProcessing = new ChangeProcessingConfig
            {
                Reader = new ReaderConfig
                {
                    Kind = GenericDriverKinds.Watermark,
                    Options = new Dictionary<string, string> { ["watermarkColumn"] = "UpdatedAt" },
                },
                Cache = new CacheConfig { Kind = "MsSqlStagingTable" },
                Writer = new WriterConfig { Kind = "MsSqlMerge" },
            },
            Reconcile = reconcile,
        }, JsonOptions)).EnsureSuccessStatusCode();

        var mappingResponse = await _client.PutAsJsonAsync(
            $"/api/replications/{replicationName}/table-mappings/{mappingName}", new TableMappingConfig
            {
                Name = mappingName,
                Sources = [new SourceTableSpec { Schema = "dbo", Table = "Orders" }],
                Targets = [new TableSpec { Schema = "dbo", Table = "Orders" }],
                // A cached, fully-mapped key — ValidateReconcile (via ValidateKeyReconcilePairing)
                // requires it the moment Reconcile.Enabled is true.
                SourceColumns = [new CachedColumn("Id", "int", false, true, false)],
                TargetColumns = [new CachedColumn("Id", "int", false, true, false)],
                ColumnMappings = [new ColumnMapping { SourceColumn = "Id", TargetColumn = "Id" }],
                DefaultSegmenting = segments is null ? [] : [.. segments],
            }, JsonOptions);
        if (!mappingResponse.IsSuccessStatusCode)
            throw new Exception($"{(int)mappingResponse.StatusCode}: {await mappingResponse.Content.ReadAsStringAsync()}");

        return replicationName;
    }

    /// <summary>Fabricates a successful Primary pass having just read <paramref name="rowsRead"/>
    /// rows — the after-change trigger's own input — without running a real one.</summary>
    private void RecordPrimaryPass(string replicationName, string mappingName, long rowsRead)
    {
        var runId = _workQueueStore.Enqueue(replicationName, RunKind.Primary, mappingName);
        _taskRunStore.CompleteRun(runId, RunStatus.Succeeded, rowsRead, rowsRead, errorSummary: null);
    }

    private int ReconcileRunCount(string replicationName, string mappingName) =>
        _taskRunStore.GetRunHistory(replicationName, RunKind.ReconcileDeletes, mappingName, limit: 50).Count;

    private static readonly MethodInfo TickMethod = typeof(SchedulerService)
        .GetMethod("TickAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;

    private static Task TickAsync(SchedulerService scheduler) =>
        (Task)TickMethod.Invoke(scheduler, [CancellationToken.None])!;

    private SchedulerService BuildScheduler() => new(
        factory.Services.GetRequiredService<ConfigRepository>(),
        _taskRunStore,
        _workQueueStore,
        factory.Services.GetRequiredService<ProcessSupervisor>(),
        factory.Services.GetRequiredService<ChangePollingGate>(),
        factory.Services.GetRequiredService<ChangeWatermarkStore>(),
        factory.Services.GetRequiredService<DriverRegistry>(),
        factory.Services.GetRequiredService<ReconcileService>(),
        NullLogger<SchedulerService>.Instance);

    [Fact]
    public async Task ACadenceDueMapping_EnqueuesOneSweepPerDefaultSegment()
    {
        var replicationName = await SetUpMappingAsync(
            new ReconcileConfig { Enabled = true, Every = new SchedulingConfig { Mode = ScheduleMode.Continuous, FrequencySeconds = 3600 } },
            segments: [new RangeSegment("Id", "1", "100"), new RangeSegment("Id", "100", "200")]);

        await TickAsync(BuildScheduler());

        // Never swept before — due immediately, the same "never run before" posture SchedulingEvaluator
        // already has for a Primary pass — and one work item per configured segment, exactly like a
        // standalone reload replication's own DefaultSegmenting.
        Assert.Equal(2, ReconcileRunCount(replicationName, "orders"));
    }

    [Fact]
    public async Task NoAfterChangeStrategyAndNoEvery_NeverEnqueuesEvenAfterChanges()
    {
        var replicationName = await SetUpMappingAsync(new ReconcileConfig { Enabled = true });
        RecordPrimaryPass(replicationName, "orders", rowsRead: 500);

        await TickAsync(BuildScheduler());

        Assert.Equal(0, ReconcileRunCount(replicationName, "orders"));
    }

    [Fact]
    public async Task AfterAnyChangeStrategy_EnqueuesOnceAfterRowsAreRead_AndNotAgainUntilMoreArrive()
    {
        var replicationName = await SetUpMappingAsync(new ReconcileConfig
        {
            Enabled = true,
            AfterChange = new AfterAnyChangeStrategy(),
            // Required by ValidateReconcile whenever AfterChange is set — also the after-change floor.
            Every = new SchedulingConfig { Mode = ScheduleMode.Continuous, FrequencySeconds = 3600 },
        });

        // No Primary pass has ever read anything yet — nothing to react to.
        await TickAsync(BuildScheduler());
        Assert.Equal(0, ReconcileRunCount(replicationName, "orders"));

        RecordPrimaryPass(replicationName, "orders", rowsRead: 3);
        await TickAsync(BuildScheduler());
        Assert.Equal(1, ReconcileRunCount(replicationName, "orders"));

        // A second tick with nothing new to react to must not enqueue a second sweep — the trigger is
        // "since the last sweep", and the sweep that just ran moved that cutoff forward.
        await TickAsync(BuildScheduler());
        Assert.Equal(1, ReconcileRunCount(replicationName, "orders"));
    }

    /// <summary>The cadence floor: an after-change trigger cannot fire more often than `Every` allows,
    /// even when rows keep arriving between ticks.</summary>
    [Fact]
    public async Task AfterAnyChangeStrategy_NeverFiresMoreOftenThanTheCadenceAllows()
    {
        var replicationName = await SetUpMappingAsync(new ReconcileConfig
        {
            Enabled = true,
            AfterChange = new AfterAnyChangeStrategy(),
            // An hour — long enough that "again immediately" is unambiguously not due.
            Every = new SchedulingConfig { Mode = ScheduleMode.Continuous, FrequencySeconds = 3600 },
        });

        RecordPrimaryPass(replicationName, "orders", rowsRead: 3);
        await TickAsync(BuildScheduler());
        Assert.Equal(1, ReconcileRunCount(replicationName, "orders"));

        // More rows arrive, but the cadence floor (an hour, just started) is nowhere near due again.
        RecordPrimaryPass(replicationName, "orders", rowsRead: 7);
        await TickAsync(BuildScheduler());
        Assert.Equal(1, ReconcileRunCount(replicationName, "orders"));
    }

    [Fact]
    public async Task ASweepAlreadyInFlight_IsNotDuplicated()
    {
        var replicationName = await SetUpMappingAsync(
            new ReconcileConfig { Enabled = true, Every = new SchedulingConfig { Mode = ScheduleMode.Continuous, FrequencySeconds = 3600 } });

        var scheduler = BuildScheduler();
        await TickAsync(scheduler);
        Assert.Equal(1, ReconcileRunCount(replicationName, "orders"));

        // Still Pending (nothing claimed it — no worker was actually spawned against a real container
        // here) — a second tick before it's due again must not queue a second one regardless.
        await TickAsync(scheduler);
        Assert.Equal(1, ReconcileRunCount(replicationName, "orders"));
    }
}
