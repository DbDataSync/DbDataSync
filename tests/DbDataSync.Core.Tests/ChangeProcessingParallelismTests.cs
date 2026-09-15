using ClrKernel.Core.Secrets;
using DbDataSync.Core.Config;
using DbDataSync.Core.Git;
using LibGit2Sharp;

namespace DbDataSync.Core.Tests;

/// <summary>
/// <see cref="ChangeProcessingConfig.DegreeOfParallelism"/> is what a replication's worker is spawned
/// with — how many of its table mappings it processes at once. These pin that it survives a
/// round trip (with the same <c>[DefaultValue]</c> care <see cref="DisablingRoundTripTests"/> covers
/// for <c>Enabled</c>), that a config written by a build which still had the retired per-stage
/// <c>parallelism</c> key still loads, and that a number below one is refused.
/// </summary>
public sealed class ChangeProcessingParallelismTests : IDisposable
{
    private static readonly GitAuthor Author = new("Test", "test@example.com");

    private readonly string _root = Directory.CreateTempSubdirectory("dbdatasync-dop-").FullName;
    private readonly ConfigRepository _config;

    public ChangeProcessingParallelismTests()
    {
        Repository.Init(_root);
        _config = new ConfigRepository(
            Path.Combine(_root, "config"), new GitCommitService(_root),
            SecretStore.ForProviders([new InMemorySecretProvider()]));
    }

    public void Dispose() => GitTempDirectory.DeleteRecursively(_root);

    private static ReplicationTaskConfig Task(int? degreeOfParallelism = null, int? bulkLoadDegreeOfParallelism = null) => new()
    {
        Name = "sales",
        Scheduling = new SchedulingConfig { Mode = ScheduleMode.Continuous, FrequencySeconds = 30 },
        ChangeProcessing = new ChangeProcessingConfig
        {
            Reader = new ReaderConfig { Kind = "MsSqlChangeTracking" },
            Cache = new CacheConfig { Kind = "MsSqlStagingTable" },
            Writer = new WriterConfig { Kind = "MsSqlMerge" },
            DegreeOfParallelism = degreeOfParallelism ?? ChangeProcessingConfig.DefaultDegreeOfParallelism,
            BulkLoadDegreeOfParallelism = bulkLoadDegreeOfParallelism ?? ChangeProcessingConfig.DefaultDegreeOfParallelism,
        },
    };

    private string TaskYaml() =>
        File.ReadAllText(Path.Combine(_root, "config", "replications", "sales", "task.yaml"));

    [Fact]
    public void ANonDefaultDegree_ReloadsAsSet()
    {
        _config.SaveReplicationTask(Task(degreeOfParallelism: 12), Author);

        Assert.Equal(12, _config.LoadReplicationTask("sales").ChangeProcessing.DegreeOfParallelism);
    }

    [Fact]
    public void TheDefault_IsNotSpelledOutInTheFile_ButStillReloadsAsTheDefault()
    {
        _config.SaveReplicationTask(Task(), Author);

        Assert.DoesNotContain("degreeOfParallelism", TaskYaml());
        Assert.Equal(
            ChangeProcessingConfig.DefaultDegreeOfParallelism,
            _config.LoadReplicationTask("sales").ChangeProcessing.DegreeOfParallelism);
    }

    [Fact]
    public void RaisingThenRestoringTheDefault_EndsAtTheDefaultAndUnwritten()
    {
        _config.SaveReplicationTask(Task(degreeOfParallelism: 8), Author);
        _config.SaveReplicationTask(Task(degreeOfParallelism: ChangeProcessingConfig.DefaultDegreeOfParallelism), Author);

        Assert.DoesNotContain("degreeOfParallelism", TaskYaml());
        Assert.Equal(
            ChangeProcessingConfig.DefaultDegreeOfParallelism,
            _config.LoadReplicationTask("sales").ChangeProcessing.DegreeOfParallelism);
    }

    /// <summary>
    /// The SPA wrote <c>parallelism: 1</c> under <c>reader</c>/<c>writer</c> for every replication it
    /// created (1 is not <c>default(int)</c>, so the serializer kept it). That key no longer maps to
    /// anything — loading such a file must not throw, and the replication comes back on the default.
    /// </summary>
    [Fact]
    public void AConfigWithTheRetiredPerStageParallelismKey_StillLoads()
    {
        var dir = Path.Combine(_root, "config", "replications", "legacy");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "task.yaml"), """
            name: legacy
            scheduling:
              mode: Continuous
              frequencySeconds: 30
            changeProcessing:
              reader:
                kind: MsSqlChangeTracking
                parallelism: 1
              cache:
                kind: MsSqlStagingTable
              writer:
                kind: MsSqlMerge
                parallelism: 1
            """);

        var loaded = _config.LoadReplicationTask("legacy");

        Assert.Equal("MsSqlChangeTracking", loaded.ChangeProcessing.Reader.Kind);
        Assert.Equal(
            ChangeProcessingConfig.DefaultDegreeOfParallelism,
            loaded.ChangeProcessing.DegreeOfParallelism);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-3)]
    public void ADegreeBelowOne_IsRefused(int degree)
    {
        var problem = Assert.Throws<ConfigValidationException>(
            () => _config.SaveReplicationTask(Task(degreeOfParallelism: degree), Author));

        Assert.Contains("at least 1", problem.Message);
    }

    [Fact]
    public void TheBulkLoadLaneHasItsOwnDegree_RoundTrippedIndependently()
    {
        _config.SaveReplicationTask(Task(degreeOfParallelism: 8, bulkLoadDegreeOfParallelism: 2), Author);

        var loaded = _config.LoadReplicationTask("sales").ChangeProcessing;
        Assert.Equal(8, loaded.DegreeOfParallelism);
        Assert.Equal(2, loaded.BulkLoadDegreeOfParallelism);
        Assert.Contains("bulkLoadDegreeOfParallelism: 2", TaskYaml());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-3)]
    public void ABulkLoadDegreeBelowOne_IsRefused(int degree)
    {
        var problem = Assert.Throws<ConfigValidationException>(
            () => _config.SaveReplicationTask(Task(bulkLoadDegreeOfParallelism: degree), Author));

        Assert.Contains("at least 1", problem.Message);
    }
}
