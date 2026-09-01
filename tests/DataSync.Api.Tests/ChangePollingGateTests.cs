using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using DataSync.Api.Services;
using DataSync.Core.Config;
using DataSync.Core.Sql;
using DataSync.Drivers.MsSql;
using DataSync.State;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DataSync.Api.Tests;

/// <summary>
/// The scheduler's polling gate — see phase 75. One source round-trip per
/// <c>(ConnectionName, Database, SourceKind)</c> group per tick in place of one per due mapping, and
/// a skip decision made against each mapping's own watermark rather than against a remembered
/// counter.
/// <para>
/// The counter fetch is the one thing faked here, because it is the one thing that talks to a source.
/// Everything else — the config, the driver registry that names the dialect the watermark key is
/// spelled in, the two state tables — is the real thing the API is wired with, so what these assert
/// is the gate's behaviour and not a rehearsal of it.
/// </para>
/// </summary>
public sealed class ChangePollingGateTests(TestApiFactory factory) : IClassFixture<TestApiFactory>
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly HttpClient _client = factory.CreateClient();

    /// <summary>An LSN as CDC's own encoding spells it — hex, which is what ChangeWatermarks holds
    /// and therefore what the gate compares.</summary>
    private static string Lsn(params byte[] bytes) => MsSqlCdcCatalog.ToWatermark(bytes);

    private (ChangePollingGate Gate, FakeChangeCounterSource Source) BuildGate()
    {
        var source = new FakeChangeCounterSource();
        var gate = new ChangePollingGate(
            factory.Services.GetRequiredService<ChangeSourceResolver>(),
            source,
            factory.Services.GetRequiredService<ChangeWatermarkStore>(),
            factory.Services.GetRequiredService<ChangeCheckStore>(),
            NullLogger<ChangePollingGate>.Instance);
        return (gate, source);
    }

    /// <summary>
    /// A replication on one MsSql connection, with the named mappings. Each entry's reader kind is
    /// the mapping's own override, so one replication can carry both mechanisms — which is exactly
    /// the arrangement revision 1 of the phase doc exists to keep apart.
    /// </summary>
    private async Task<ReplicationTaskConfig> SetUpAsync(
        params (string Mapping, string ReaderKind)[] mappings)
    {
        var replicationName = $"gate-{Guid.NewGuid():N}";

        (await _client.PutAsJsonAsync("/api/connections/gate-src", new ConnectionInput
        {
            Name = "gate-src",
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
                Source = new EndpointRef { ConnectionName = "gate-src", Database = "App" },
                Target = new EndpointRef { ConnectionName = "gate-src", Database = "DW" },
            },
            Scheduling = new SchedulingConfig { Mode = ScheduleMode.Continuous, FrequencySeconds = 3600 },
            ChangeProcessing = new ChangeProcessingConfig
            {
                Reader = new ReaderConfig { Kind = MsSqlDriverKinds.ChangeTracking },
                Cache = new CacheConfig { Kind = MsSqlDriverKinds.StagingTable },
                Writer = new WriterConfig { Kind = MsSqlDriverKinds.Merge },
            },
        }, JsonOptions)).EnsureSuccessStatusCode();

        foreach (var (mapping, readerKind) in mappings)
        {
            (await _client.PutAsJsonAsync(
                $"/api/replications/{replicationName}/table-mappings/{mapping}", new TableMappingConfig
                {
                    Name = mapping,
                    Sources = [new SourceTableSpec { Schema = "dbo", Table = mapping }],
                    Targets = [new TableSpec { Schema = "dbo", Table = mapping }],
                    ReaderOverride = new ReaderConfig { Kind = readerKind },
                }, JsonOptions)).EnsureSuccessStatusCode();
        }

        return factory.Services.GetRequiredService<ConfigRepository>().LoadReplicationTask(replicationName);
    }

    /// <summary>Writes a mapping's watermark under the key the gate will read it by — built the same
    /// way, through the source's own dialect, so a change to WatermarkKey.Build cannot make these
    /// pass against a key nothing else uses.</summary>
    private void SetWatermark(ReplicationTaskConfig task, string mapping, string watermark)
    {
        var source = EndpointResolution.ResolveSource(
            task, new SourceTableSpec { Schema = "dbo", Table = mapping });
        factory.Services.GetRequiredService<ChangeWatermarkStore>().SetWatermark(
            task.Name, mapping, WatermarkKey.Build(source, MsSqlDialect.Instance), watermark);
    }

    [Fact]
    public async Task TwoMappingsOnOneSourceGroup_FetchTheCounterOnce()
    {
        var task = await SetUpAsync(
            ("orders", MsSqlDriverKinds.ChangeTracking),
            ("shipments", MsSqlDriverKinds.ChangeTracking));

        var (gate, source) = BuildGate();
        source.Set("gate-src", "App", MsSqlDriverKinds.ChangeTracking, "100");

        var admitted = await gate.AdmitAsync(task, ["orders", "shipments"], CancellationToken.None);

        // The whole point of the phase: two mappings, one round-trip. Neither has a watermark, so
        // both are dispatched — which is a separate question from how many times the source was asked.
        Assert.Single(source.Fetches);
        Assert.Equal(["orders", "shipments"], admitted);
    }

    [Fact]
    public async Task CdcAndChangeTrackingInOneDatabase_AreTwoGroupsWithTwoAuditRows()
    {
        var task = await SetUpAsync(
            ("ctorders", MsSqlDriverKinds.ChangeTracking),
            ("cdcorders", MsSqlDriverKinds.Cdc));

        var (gate, source) = BuildGate();
        source.Set("gate-src", "App", MsSqlDriverKinds.ChangeTracking, "100");
        source.Set("gate-src", "App", MsSqlDriverKinds.Cdc, Lsn(0, 0, 0, 42, 0, 0, 0, 171, 0, 3));

        await gate.AdmitAsync(task, ["ctorders", "cdcorders"], CancellationToken.None);

        // Same connection, same database — and still two fetches, because a max LSN and a change
        // version are not the same quantity and a shared row would hold whichever polled last in a
        // format the other cannot read.
        Assert.Equal(2, source.Fetches.Count);
        Assert.Contains(("gate-src", "App", MsSqlDriverKinds.ChangeTracking), source.Fetches);
        Assert.Contains(("gate-src", "App", MsSqlDriverKinds.Cdc), source.Fetches);

        var checks = factory.Services.GetRequiredService<ChangeCheckStore>().ListChecks()
            .Where(c => c.ConnectionName == "gate-src" && c.SourceDatabase == "App")
            .ToList();

        Assert.Equal("100", Assert.Single(checks, c => c.SourceKind == MsSqlDriverKinds.ChangeTracking).Value);
        Assert.Equal(
            Lsn(0, 0, 0, 42, 0, 0, 0, 171, 0, 3),
            Assert.Single(checks, c => c.SourceKind == MsSqlDriverKinds.Cdc).Value);
    }

    [Fact]
    public async Task AMappingMidDrainIsDispatched_WhileACaughtUpSiblingIsSkipped()
    {
        var task = await SetUpAsync(
            ("draining", MsSqlDriverKinds.ChangeTracking),
            ("caughtup", MsSqlDriverKinds.ChangeTracking));

        // The scenario the gate must not get wrong. 'draining' is partway through a backlog under a
        // row cap — its watermark is at 40 of a source that reached 100 some time ago — and nothing
        // has been written since, so the counter has not moved for several ticks. A gate comparing
        // the counter against its own last reading would call that "nothing to do" and stall a
        // mapping that has sixty versions of real, already-captured work in front of it.
        SetWatermark(task, "draining", "40");
        SetWatermark(task, "caughtup", "100");

        var (gate, source) = BuildGate();
        source.Set("gate-src", "App", MsSqlDriverKinds.ChangeTracking, "100");

        var admitted = await gate.AdmitAsync(task, ["draining", "caughtup"], CancellationToken.None);

        Assert.Equal(["draining"], admitted);
        Assert.Single(source.Fetches);
    }

    [Fact]
    public async Task AMappingWithNoWatermarkIsAlwaysDispatched()
    {
        var task = await SetUpAsync(
            ("firstpass", MsSqlDriverKinds.ChangeTracking),
            ("settled", MsSqlDriverKinds.ChangeTracking));

        SetWatermark(task, "settled", "100");

        var (gate, source) = BuildGate();
        source.Set("gate-src", "App", MsSqlDriverKinds.ChangeTracking, "100");

        var admitted = await gate.AdmitAsync(task, ["firstpass", "settled"], CancellationToken.None);

        // Nothing to be caught up with. A first pass is not this mechanism's to gate, and reading the
        // absence of a watermark as "at zero, below the counter" would give the right answer here by
        // accident and the wrong one for a source whose counter is itself zero.
        Assert.Equal(["firstpass"], admitted);
    }

    [Fact]
    public async Task AnLsnAtTheDatabaseMaximum_IsSkipped()
    {
        var task = await SetUpAsync(("cdconly", MsSqlDriverKinds.Cdc));

        var max = Lsn(0, 0, 0, 42, 0, 0, 0, 171, 0, 3);
        SetWatermark(task, "cdconly", max);

        var (gate, source) = BuildGate();
        source.Set("gate-src", "App", MsSqlDriverKinds.Cdc, max);

        Assert.Empty(await gate.AdmitAsync(task, ["cdconly"], CancellationToken.None));

        // And behind it by one byte in the low position — the comparison that a string compare of two
        // equal-length hex values would also get right, and that ordering them as numbers would not.
        SetWatermark(task, "cdconly", Lsn(0, 0, 0, 42, 0, 0, 0, 171, 0, 2));
        Assert.Equal(["cdconly"], await gate.AdmitAsync(task, ["cdconly"], CancellationToken.None));
    }

    [Fact]
    public async Task ANullCounterIsNotSilence_AndDispatches()
    {
        var task = await SetUpAsync(("nocapture", MsSqlDriverKinds.Cdc));
        SetWatermark(task, "nocapture", Lsn(0, 0, 0, 42, 0, 0, 0, 171, 0, 3));

        var (gate, _) = BuildGate();
        // fn_cdc_get_max_lsn() answers null when the capture job has never run or has been stopped,
        // which is a source that cannot say where it is — not a source that says nothing changed.
        var admitted = await gate.AdmitAsync(task, ["nocapture"], CancellationToken.None);

        Assert.Equal(["nocapture"], admitted);
        var recorded = factory.Services.GetRequiredService<ChangeCheckStore>().ListChecks()
            .First(c => c.SourceKind == MsSqlDriverKinds.Cdc && c.ConnectionName == "gate-src");
        Assert.Null(recorded.Value);
    }

    [Fact]
    public async Task AnUnreachableSourceFailsOpenForItsOwnGroupOnly()
    {
        var task = await SetUpAsync(
            ("ctdown", MsSqlDriverKinds.ChangeTracking),
            ("ctdown2", MsSqlDriverKinds.ChangeTracking),
            ("cdcup", MsSqlDriverKinds.Cdc));

        // Every one of them is caught up as far as local state knows, so anything dispatched here is
        // dispatched because the gate could not answer, not because it decided there was work.
        SetWatermark(task, "ctdown", "100");
        SetWatermark(task, "ctdown2", "100");
        var max = Lsn(0, 0, 0, 42, 0, 0, 0, 171, 0, 3);
        SetWatermark(task, "cdcup", max);

        var (gate, source) = BuildGate();
        source.Unreachable.Add(("gate-src", "App", MsSqlDriverKinds.ChangeTracking));
        source.Set("gate-src", "App", MsSqlDriverKinds.Cdc, max);

        var admitted = await gate.AdmitAsync(
            task, ["ctdown", "ctdown2", "cdcup"], CancellationToken.None);

        // The whole failed group dispatches, exactly as the scheduler behaved before this phase
        // existed; the healthy group in the same tick is still gated, and its caught-up mapping is
        // still skipped. A source being down must not be a way to suppress replication, and must not
        // be a way to stop suppressing it everywhere else either.
        Assert.Equal(["ctdown", "ctdown2"], admitted);
    }

    [Fact]
    public async Task AnUngatedReaderKindIsDispatchedUntouched()
    {
        var task = await SetUpAsync(("reload", MsSqlDriverKinds.BatchReload));

        var (gate, source) = BuildGate();
        var admitted = await gate.AdmitAsync(task, ["reload"], CancellationToken.None);

        // No database-wide change counter exists for a batch reload — nor for the generic watermark
        // reader, nor for trigger audit — so there is nothing to ask and nothing to skip on. Every
        // reader kind but CDC and Change Tracking schedules exactly as it did before phase 75.
        Assert.Equal(["reload"], admitted);
        Assert.Empty(source.Fetches);
    }

    /// <summary>
    /// The composition phase 84 depends on, asserted rather than assumed. CDC reads are now capped by
    /// default, so a mapping with a backlog drains over many passes — and between those passes nothing
    /// is written at the source, so the database-wide max LSN does not move at all. Across every one of
    /// those ticks the gate must keep dispatching the mapping, and then stop the tick it catches up.
    /// <para>
    /// This is the failure a cached "last checked" counter would produce and the reason phase 75's gate
    /// does not keep one: the counter is identical on every tick here, and a gate comparing it against
    /// its own previous reading would call the first tick busy and every later one quiet, stalling the
    /// mapping mid-drain for as long as its schedule kept coming round. Several ticks rather than one,
    /// because a single-tick assertion cannot tell "compares against the mapping" from "compares
    /// against a counter it has not seen before".
    /// </para>
    /// </summary>
    [Fact]
    public async Task ACdcMappingDrainingUnderARowCap_IsDispatchedOnEveryTickUntilItCatchesUp()
    {
        var task = await SetUpAsync(("draining", MsSqlDriverKinds.Cdc));

        // The source reached LSN 10 some time ago and has been quiet since; the mapping is at 0.
        var max = Lsn(0, 0, 0, 0, 0, 0, 0, 0, 0, 10);
        SetWatermark(task, "draining", Lsn(0, 0, 0, 0, 0, 0, 0, 0, 0, 0));

        var (gate, source) = BuildGate();
        source.Set("gate-src", "App", MsSqlDriverKinds.Cdc, max);

        // Five ticks, five capped passes, each advancing the mapping's own watermark by two LSNs —
        // which is what a bounded read's WatermarkAfterRead does — and never touching the counter.
        for (byte reached = 2; reached <= 10; reached += 2)
        {
            var admitted = await gate.AdmitAsync(task, ["draining"], CancellationToken.None);
            Assert.Equal(["draining"], admitted);

            SetWatermark(task, "draining", Lsn(0, 0, 0, 0, 0, 0, 0, 0, 0, reached));
        }

        // Caught up, and only now skipped. The counter is the same value it was on the first tick.
        Assert.Empty(await gate.AdmitAsync(task, ["draining"], CancellationToken.None));
        Assert.Equal(6, source.Fetches.Count);
        Assert.All(source.Fetches, f => Assert.Equal(MsSqlDriverKinds.Cdc, f.Kind));
    }
}
