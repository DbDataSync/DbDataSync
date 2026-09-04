using ClrKernel.Core.Secrets;
using DbDataSync.Core.Config;
using DbDataSync.Core.Git;
using LibGit2Sharp;

namespace DbDataSync.Core.Tests;

/// <summary>
/// The phase 46 regression, at <c>DefaultReadIntent</c>: a nullable property survives round-tripping an
/// explicit value equal to the application default, which a non-nullable one sitting at its own default
/// would not — the YAML serializer omits defaults, comparing against <c>default(T)</c> unless told
/// otherwise, and <c>default(ReadIntent?)</c> is null rather than <see cref="ReadIntent.InitialLoad"/>.
/// Asserted rather than assumed, per phase 100's verification list.
/// </summary>
public sealed class ReadIntentYamlRoundTripTests : IDisposable
{
    private static readonly GitAuthor Author = new("Test", "test@example.com");

    private readonly string _root = Directory.CreateTempSubdirectory("dbdatasync-read-intent-yaml-").FullName;
    private readonly ConfigRepository _config;

    public ReadIntentYamlRoundTripTests()
    {
        Repository.Init(_root);
        _config = new ConfigRepository(
            Path.Combine(_root, "config"), new GitCommitService(_root),
            SecretStore.ForProviders([new InMemorySecretProvider()]));
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private ReplicationTaskConfig Task(ReadIntent? defaultReadIntent) => new()
    {
        Name = "sales",
        Scheduling = new SchedulingConfig { Mode = ScheduleMode.Continuous, FrequencySeconds = 30 },
        ChangeProcessing = new ChangeProcessingConfig
        {
            Reader = new ReaderConfig { Kind = "MsSqlChangeTracking" },
            Cache = new CacheConfig { Kind = "MsSqlStagingTable" },
            Writer = new WriterConfig { Kind = "MsSqlMerge" },
        },
        DefaultReadIntent = defaultReadIntent,
    };

    private TableMappingConfig Mapping(ReadIntent? defaultReadIntent) => new()
    {
        Name = "orders",
        Sources = [new SourceTableSpec { Table = "Orders" }],
        Targets = [new TableSpec { Table = "Orders" }],
        DefaultReadIntent = defaultReadIntent,
    };

    /// <summary>Nothing configured round-trips as nothing configured — the ordinary case must not
    /// regress while fixing the explicit-default one.</summary>
    [Fact]
    public void ReplicationWithNoDefaultReadIntent_ReloadsNull()
    {
        _config.SaveReplicationTask(Task(null), Author);
        Assert.Null(_config.LoadReplicationTask("sales").DefaultReadIntent);
    }

    /// <summary>
    /// The regression itself: <c>InitialLoad</c> is the application default that
    /// <see cref="ReadIntentResolution.Default"/> falls back to when nothing is stored — exactly the
    /// value phase 46 found silently swallowed for <c>Enabled</c>'s <c>true</c>.
    /// </summary>
    [Fact]
    public void ReplicationExplicitlyChoosingTheApplicationDefault_StillReloadsWithItSet()
    {
        _config.SaveReplicationTask(Task(ReadIntent.InitialLoad), Author);

        var reloaded = _config.LoadReplicationTask("sales");
        Assert.Equal(ReadIntent.InitialLoad, reloaded.DefaultReadIntent);
    }

    [Fact]
    public void ReplicationChoosingANonDefaultIntent_ReloadsIt()
    {
        _config.SaveReplicationTask(Task(ReadIntent.ChangesFromEarliest), Author);
        Assert.Equal(ReadIntent.ChangesFromEarliest, _config.LoadReplicationTask("sales").DefaultReadIntent);
    }

    [Fact]
    public void MappingWithNoDefaultReadIntent_ReloadsNull()
    {
        _config.SaveReplicationTask(Task(null), Author);
        _config.SaveTableMapping("sales", Mapping(null), Author);

        Assert.Null(_config.LoadTableMapping("sales", "orders").DefaultReadIntent);
    }

    /// <summary>
    /// The same regression one level down: a mapping explicitly choosing <c>InitialLoad</c> under a
    /// replication defaulting to something else has to survive as a mapping-level choice, not collapse
    /// into "the mapping said nothing" just because the value happens to equal the type's own default.
    /// </summary>
    [Fact]
    public void MappingExplicitlyChoosingInitialLoadUnderADifferentReplicationDefault_StillReloadsWithItSet()
    {
        _config.SaveReplicationTask(Task(ReadIntent.ChangesFromLatest), Author);
        _config.SaveTableMapping("sales", Mapping(ReadIntent.InitialLoad), Author);

        var task = _config.LoadReplicationTask("sales");
        var mapping = _config.LoadTableMapping("sales", "orders");

        Assert.Equal(ReadIntent.InitialLoad, mapping.DefaultReadIntent);
        Assert.Equal(ReadIntent.InitialLoad, ReadIntentResolution.Default(task, mapping));
        Assert.Equal(BindingLevel.Mapping, ReadIntentResolution.LevelOf(mapping));
    }

    /// <summary>The default is still omitted from the file when nobody has said anything — this setting
    /// does not turn every replication's YAML into a wall of stated defaults.</summary>
    [Fact]
    public void WithNoDefaultReadIntent_TheFieldIsOmittedFromTheFile()
    {
        _config.SaveReplicationTask(Task(null), Author);
        var yaml = File.ReadAllText(Path.Combine(_root, "config", "replications", "sales", "task.yaml"));

        Assert.DoesNotContain("defaultReadIntent", yaml, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void WithADefaultReadIntentSet_TheFieldIsWrittenToTheFile()
    {
        _config.SaveReplicationTask(Task(ReadIntent.InitialLoad), Author);
        var yaml = File.ReadAllText(Path.Combine(_root, "config", "replications", "sales", "task.yaml"));

        Assert.Contains("defaultReadIntent", yaml, StringComparison.OrdinalIgnoreCase);
    }
}
