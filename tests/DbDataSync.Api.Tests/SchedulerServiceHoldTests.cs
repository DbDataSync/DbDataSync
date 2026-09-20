using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using DbDataSync.Api.Services;
using DbDataSync.Core.Config;
using DbDataSync.Core.Sql;
using DbDataSync.Drivers.Abstractions;
using DbDataSync.Drivers.Generic;
using DbDataSync.State;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DbDataSync.Api.Tests;

/// <summary>
/// The actual bug phase 101 exists to fix: a mapping whose source position expired used to fail on
/// every scheduled tick, forever — another failed run and another notification each interval, burying
/// every other failure in that replication's history. See
/// architecture/implementation/done/phase-101-readers-honour-the-read-intent.md and
/// <see cref="SchedulerService.FilterHeld"/>.
/// <para>
/// <see cref="SchedulerService.TickAsync"/> is invoked directly by reflection rather than waiting out
/// its real five-second timer — the interesting behaviour is what one tick does, not the polling
/// interval, and the same method is what every real tick calls.
/// </para>
/// </summary>
public sealed class SchedulerServiceHoldTests(TestApiFactory factory) : IClassFixture<TestApiFactory>
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly HttpClient _client = factory.CreateClient();

    /// <summary>
    /// A replication on the generic Watermark reader — deliberately not CDC/Change Tracking, so
    /// <see cref="ChangePollingGate"/> never needs a real source round-trip to admit it (an ungated
    /// reader kind is always dispatched unfiltered by the gate; see <c>ChangeCounters.IsGated</c>).
    /// What is under test here is the hold filter that runs before the gate, not the gate itself.
    /// </summary>
    private async Task<(ReplicationTaskConfig Task, TableMappingConfig Mapping)> SetUpHeldMappingAsync(
        [System.Runtime.CompilerServices.CallerMemberName] string testName = "")
    {
        var replicationName = $"hold-{testName}-{Guid.NewGuid():N}";
        const string mappingName = "orders";

        (await _client.PutAsJsonAsync($"/api/connections/hold-src-{testName}", new ConnectionInput
        {
            Name = $"hold-src-{testName}",
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
                Source = new EndpointRef { ConnectionName = $"hold-src-{testName}", Database = "App" },
                Target = new EndpointRef { ConnectionName = $"hold-src-{testName}", Database = "DW" },
            },
            // Frequency is irrelevant to a mapping that has never run — SchedulingEvaluator.IsDue is
            // true unconditionally the first time — but Continuous is what SchedulerService's continuous
            // path evaluates per-mapping, which is the code path a held mapping has to be filtered out
            // of.
            Scheduling = new SchedulingConfig { Mode = ScheduleMode.Continuous, FrequencySeconds = 3600 },
            ChangeProcessing = new ChangeProcessingConfig
            {
                // watermarkColumn is a required parameter on this reader (WatermarkReader.Parameters);
                // ParameterCheck enforces that on save even at replication level, so the reader config
                // needs one, not just a Kind.
                Reader = new ReaderConfig
                {
                    Kind = GenericDriverKinds.Watermark,
                    Options = new Dictionary<string, string> { ["watermarkColumn"] = "UpdatedAt" },
                },
                Cache = new CacheConfig { Kind = "MsSqlStagingTable" },
                Writer = new WriterConfig { Kind = "MsSqlMerge" },
            },
        }, JsonOptions)).EnsureSuccessStatusCode();

        (await _client.PutAsJsonAsync(
            $"/api/replications/{replicationName}/table-mappings/{mappingName}", new TableMappingConfig
            {
                Name = mappingName,
                Sources = [new SourceTableSpec { Schema = "dbo", Table = "Orders" }],
                Targets = [new TableSpec { Schema = "dbo", Table = "Orders" }],
            }, JsonOptions)).EnsureSuccessStatusCode();

        var task = factory.Services.GetRequiredService<ConfigRepository>().LoadReplicationTask(replicationName);
        var mapping = factory.Services.GetRequiredService<ConfigRepository>().LoadTableMapping(replicationName, mappingName);
        return (task, mapping);
    }

    private string WatermarkKeyFor(ReplicationTaskConfig task, TableMappingConfig mapping)
    {
        var source = EndpointResolution.ResolveSource(task, mapping.Sources[0]);
        return WatermarkKey.Build(source, MsSqlDialect.Instance);
    }

    private SchedulerService BuildScheduler() => new(
        factory.Services.GetRequiredService<ConfigRepository>(),
        factory.Services.GetRequiredService<TaskRunStore>(),
        factory.Services.GetRequiredService<WorkQueueStore>(),
        factory.Services.GetRequiredService<ProcessSupervisor>(),
        factory.Services.GetRequiredService<ChangePollingGate>(),
        factory.Services.GetRequiredService<ChangeWatermarkStore>(),
        factory.Services.GetRequiredService<DriverRegistry>(),
        factory.Services.GetRequiredService<ReconcileService>(),
        factory.Services.GetRequiredService<UpdateDrainState>(),
        NullLogger<SchedulerService>.Instance);

    private static readonly MethodInfo TickMethod = typeof(SchedulerService)
        .GetMethod("TickAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;

    private static Task TickAsync(SchedulerService scheduler) =>
        (Task)TickMethod.Invoke(scheduler, [CancellationToken.None])!;

    /// <summary>
    /// No HTTP call, so it is unaffected by TestApiFactory's Negotiate/Kestrel limitation in this
    /// environment (see the other tests in this file) — a smoke check that every dependency
    /// <see cref="SchedulerService"/>'s constructor needs actually resolves from the real DI container,
    /// independent of whether a tick can be driven end-to-end here.
    /// </summary>
    /// <summary>
    /// Phase 159: while an update drains, the scheduler starts nothing new. The contrast is the point — the same
    /// due mapping is dispatched by the very next tick once the drain is over, so it is the gate and nothing else
    /// that held it back.
    /// </summary>
    [Fact]
    public async Task NothingIsDispatched_WhileAnUpdateDrains_ButTheNextTickAfterwardsDoesDispatch()
    {
        var (task, mapping) = await SetUpHeldMappingAsync();
        var taskRuns = factory.Services.GetRequiredService<TaskRunStore>();
        var drain = factory.Services.GetRequiredService<UpdateDrainState>();

        drain.Begin();
        try
        {
            await TickAsync(BuildScheduler());
            Assert.Empty(taskRuns.GetRunHistory(task.Name, RunKind.Primary));
        }
        finally
        {
            drain.End();
        }

        await TickAsync(BuildScheduler());
        Assert.NotEmpty(taskRuns.GetRunHistory(task.Name, RunKind.Primary));
        Assert.False(string.IsNullOrEmpty(mapping.Name));
    }

    [Fact]
    public void BuildScheduler_ResolvesEveryDependency() => Assert.NotNull(BuildScheduler());

    [Fact]
    public async Task AHeldMapping_IsNotDispatchedOnTheNextTick()
    {
        var (task, mapping) = await SetUpHeldMappingAsync();
        var watermarks = factory.Services.GetRequiredService<ChangeWatermarkStore>();
        var key = WatermarkKeyFor(task, mapping);
        watermarks.SetReadHold(task.Name, mapping.Name, key, ReadHold.PositionExpired);

        await TickAsync(BuildScheduler());

        var runs = factory.Services.GetRequiredService<TaskRunStore>()
            .GetRunHistory(task.Name, RunKind.Primary);
        Assert.Empty(runs);
    }

    /// <summary>
    /// The other half of the same fix: because a held mapping is never dispatched again, it never fails
    /// a second time, and so it is never notified about a second time either — the "one notification
    /// per hold, not one per tick" guarantee falls out of the dispatch filter rather than needing its
    /// own logic. Three ticks stand in for "every tick forever."
    /// </summary>
    [Fact]
    public async Task AHeldMapping_IsNotifiedAboutOnce_NotOncePerTick()
    {
        var (task, mapping) = await SetUpHeldMappingAsync();
        var watermarks = factory.Services.GetRequiredService<ChangeWatermarkStore>();
        var taskRuns = factory.Services.GetRequiredService<TaskRunStore>();
        var workQueue = factory.Services.GetRequiredService<WorkQueueStore>();
        var key = WatermarkKeyFor(task, mapping);

        // The one real failure, recorded the way a runner would — this is what raises the single
        // notification and sets the hold, exactly as RunExecutor does on PositionExpiredException.
        var runId = workQueue.Enqueue(task.Name, RunKind.Primary, mapping.Name);
        taskRuns.CompleteRun(runId, RunStatus.Failed, 0, 0, "position gone", RunFailureKinds.PositionExpired);
        watermarks.SetReadHold(task.Name, mapping.Name, key, ReadHold.PositionExpired);

        var notifications = factory.Services.GetRequiredService<NotificationStore>();
        var before = notifications.List().Count(n => n.MappingName == mapping.Name);
        Assert.Equal(1, before);

        var scheduler = BuildScheduler();
        await TickAsync(scheduler);
        await TickAsync(scheduler);
        await TickAsync(scheduler);

        var after = notifications.List().Count(n => n.MappingName == mapping.Name);
        Assert.Equal(1, after);

        // And no new run was ever queued for it across all three ticks.
        Assert.Single(taskRuns.GetRunHistory(task.Name, RunKind.Primary));
    }

    /// <summary>
    /// Phase 134: <see cref="ReadHold.Loading"/> is a new value, not new logic — <see cref="SchedulerService.FilterHeld"/>
    /// already excludes any hold that isn't <see cref="ReadHold.None"/> generically, so a mapping mid
    /// initial-load is filtered out for free, the same as <see cref="ReadHold.PositionExpired"/> above.
    /// </summary>
    [Fact]
    public async Task ALoadingMapping_IsNotDispatchedOnTheNextTick()
    {
        var (task, mapping) = await SetUpHeldMappingAsync();
        var watermarks = factory.Services.GetRequiredService<ChangeWatermarkStore>();
        var key = WatermarkKeyFor(task, mapping);
        watermarks.SetReadHold(task.Name, mapping.Name, key, ReadHold.Loading);

        await TickAsync(BuildScheduler());

        var runs = factory.Services.GetRequiredService<TaskRunStore>()
            .GetRunHistory(task.Name, RunKind.Primary);
        Assert.Empty(runs);
    }

    /// <summary>
    /// The open question resolved: a hold stops a scheduled <c>Primary</c> pass, never a <c>BulkLoad</c>
    /// — a bulk load does not use the cursor a hold protects, and is a legitimate way to recover a held
    /// mapping. <see cref="SchedulerService.FilterHeld"/> only ever runs over mappings due for a
    /// scheduled Primary pass; <c>BulkLoadService</c>'s own enqueue path is untouched and never
    /// consults <see cref="ReadHold"/> at all, so this needs no scheduler involvement to prove — a
    /// direct enqueue is the same one <c>BulkLoadService</c> makes.
    /// </summary>
    [Fact]
    public async Task AHeldMappings_BulkLoadStillEnqueues()
    {
        var (task, mapping) = await SetUpHeldMappingAsync();
        var watermarks = factory.Services.GetRequiredService<ChangeWatermarkStore>();
        var workQueue = factory.Services.GetRequiredService<WorkQueueStore>();
        var key = WatermarkKeyFor(task, mapping);
        watermarks.SetReadHold(task.Name, mapping.Name, key, ReadHold.PositionExpired);

        var runId = workQueue.Enqueue(task.Name, RunKind.BulkLoad, mapping.Name, "full");

        var run = factory.Services.GetRequiredService<TaskRunStore>().GetRun(runId);
        Assert.NotNull(run);
        Assert.Equal(RunStatus.Queued, run!.Status);
    }
}
