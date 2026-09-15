using ClrKernel.Core.Secrets;
using DbDataSync.Core.Config;
using DbDataSync.Core.Git;
using LibGit2Sharp;

namespace DbDataSync.Core.Tests;

/// <summary>
/// Phase 125's <see cref="ReconcileConfig"/> — specifically its two abstract fields,
/// <see cref="DeleteGuard"/> and <see cref="AfterChangeStrategy"/>, each requiring its own hand-written
/// <c>IYamlTypeConverter</c> for the identical reason <see cref="BatchReloadSegmentYamlConverter"/> does
/// (see <see cref="DefaultSegmentingYamlRoundTripTests"/>): YamlDotNet can neither discriminate a
/// derived record on the way out nor construct an abstract base type on the way back in.
/// </summary>
public sealed class ReconcileConfigYamlRoundTripTests : IDisposable
{
    private static readonly GitAuthor Author = new("Test", "test@example.com");

    private readonly string _root = Directory.CreateTempSubdirectory("dbdatasync-reconcile-yaml-").FullName;
    private readonly ConfigRepository _config;

    public ReconcileConfigYamlRoundTripTests()
    {
        Repository.Init(_root);
        _config = new ConfigRepository(
            Path.Combine(_root, "config"), new GitCommitService(_root),
            SecretStore.ForProviders([new InMemorySecretProvider()]));
    }

    public void Dispose() => GitTempDirectory.DeleteRecursively(_root);

    private ReconcileConfig RoundTrip(ReconcileConfig reconcile)
    {
        _config.SaveReplicationTask(new ReplicationTaskConfig
        {
            Name = "r",
            Scheduling = new SchedulingConfig { Mode = ScheduleMode.Periodic },
            ChangeProcessing = new ChangeProcessingConfig
            {
                Reader = new ReaderConfig { Kind = "Watermark", Options = { ["watermarkColumn"] = "Id" } },
                Cache = new CacheConfig { Kind = "StagingTable" },
                Writer = new WriterConfig { Kind = "DeleteInsert" },
            },
            Reconcile = reconcile,
        }, Author);

        return _config.LoadReplicationTask("r").Reconcile;
    }

    [Fact]
    public void ANoneGuardAndNoAfterChangeStrategy_RoundTrip()
    {
        var loaded = RoundTrip(new ReconcileConfig
        {
            Enabled = true,
            DeleteGuard = new NoneDeleteGuard(),
            AfterChange = new NoAfterChangeStrategy(),
        });

        Assert.True(loaded.Enabled);
        Assert.IsType<NoneDeleteGuard>(loaded.DeleteGuard);
        Assert.IsType<NoAfterChangeStrategy>(loaded.AfterChange);
    }

    [Fact]
    public void ARatioGuard_PreservesItsConfiguredMaxRatio()
    {
        var loaded = RoundTrip(new ReconcileConfig { Enabled = true, DeleteGuard = new RatioDeleteGuard(0.25) });

        var guard = Assert.IsType<RatioDeleteGuard>(loaded.DeleteGuard);
        Assert.Equal(0.25, guard.MaxRatio);
    }

    [Fact]
    public void AnAfterAnyChangeStrategy_WithAnEveryCadence_RoundTrips()
    {
        var loaded = RoundTrip(new ReconcileConfig
        {
            Enabled = true,
            AfterChange = new AfterAnyChangeStrategy(),
            Every = new SchedulingConfig { Mode = ScheduleMode.Continuous, FrequencySeconds = 300 },
        });

        Assert.IsType<AfterAnyChangeStrategy>(loaded.AfterChange);
        Assert.NotNull(loaded.Every);
        Assert.Equal(ScheduleMode.Continuous, loaded.Every!.Mode);
        Assert.Equal(300, loaded.Every.FrequencySeconds);
    }

    [Fact]
    public void ADisabledReconcileConfig_WithNothingSet_LoadsWithSensibleDefaults()
    {
        // A replication that never touches Reconcile at all — the common case.
        _config.SaveReplicationTask(new ReplicationTaskConfig
        {
            Name = "r2",
            Scheduling = new SchedulingConfig { Mode = ScheduleMode.Periodic },
            ChangeProcessing = new ChangeProcessingConfig
            {
                Reader = new ReaderConfig { Kind = "Watermark", Options = { ["watermarkColumn"] = "Id" } },
                Cache = new CacheConfig { Kind = "StagingTable" },
                Writer = new WriterConfig { Kind = "DeleteInsert" },
            },
        }, Author);

        var loaded = _config.LoadReplicationTask("r2").Reconcile;

        Assert.False(loaded.Enabled);
        Assert.IsType<NoAfterChangeStrategy>(loaded.AfterChange);
        Assert.IsType<RatioDeleteGuard>(loaded.DeleteGuard);
        Assert.Null(loaded.Every);
    }
}
