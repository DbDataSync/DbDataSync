using ClrKernel.Core.Secrets;
using DataSync.Core.Config;
using DataSync.Core.Git;
using LibGit2Sharp;

namespace DataSync.Core.Tests;

/// <summary>
/// Turning something off has to survive being written down.
/// <para>
/// It did not. The YAML serializer omits defaults and compares against <c>default(T)</c>, so
/// <c>false</c> — being <c>default(bool)</c> — was never written for a property whose own initializer
/// was <c>true</c>. A disabled replication reloaded as enabled, and a disabled script as enabled.
/// Nothing caught it because every test that had ever written one of these left it on.
/// </para>
/// </summary>
public sealed class DisablingRoundTripTests : IDisposable
{
    private static readonly GitAuthor Author = new("Test", "test@example.com");

    private readonly string _root = Directory.CreateTempSubdirectory("datasync-disable-").FullName;
    private readonly ConfigRepository _config;

    public DisablingRoundTripTests()
    {
        Repository.Init(_root);
        _config = new ConfigRepository(
            Path.Combine(_root, "config"), new GitCommitService(_root),
            SecretStore.ForProviders([new InMemorySecretProvider()]));
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private ReplicationTaskConfig Task(bool enabled) => new()
    {
        Name = "sales",
        Enabled = enabled,
        Scheduling = new SchedulingConfig { Mode = ScheduleMode.Continuous, FrequencySeconds = 30 },
        ChangeProcessing = new ChangeProcessingConfig
        {
            Reader = new ReaderConfig { Kind = "MsSqlChangeTracking" },
            Cache = new CacheConfig { Kind = "MsSqlStagingTable" },
            Writer = new WriterConfig { Kind = "MsSqlMerge" },
        },
    };

    [Fact]
    public void ADisabledReplication_ReloadsDisabled()
    {
        _config.SaveReplicationTask(Task(enabled: false), Author);

        Assert.False(_config.LoadReplicationTask("sales").Enabled);
    }

    /// <summary>The other direction, so the fix cannot be "always write false".</summary>
    [Fact]
    public void AnEnabledReplication_ReloadsEnabled()
    {
        _config.SaveReplicationTask(Task(enabled: true), Author);

        Assert.True(_config.LoadReplicationTask("sales").Enabled);
    }

    [Fact]
    public void DisablingThenEnabling_EndsEnabled()
    {
        _config.SaveReplicationTask(Task(enabled: false), Author);
        _config.SaveReplicationTask(Task(enabled: true), Author);

        Assert.True(_config.LoadReplicationTask("sales").Enabled);
    }

    [Fact]
    public void ADisabledScript_ReloadsDisabled()
    {
        _config.SaveScript(new ScriptDefinition
        {
            Manifest = new ScriptConfig
            {
                Name = "retired", Kind = "hook", Language = ScriptLanguage.Sql, Enabled = false,
            },
            Code = "SELECT 1;",
        }, Author);

        Assert.False(_config.LoadScript("retired").Manifest.Enabled);
    }

    /// <summary>
    /// The default is still omitted from the file, which is the point of the setting — a config that
    /// spells out every default is a config whose diffs are noise.
    /// </summary>
    [Fact]
    public void AnEnabledReplication_DoesNotSpellItOutInTheFile()
    {
        _config.SaveReplicationTask(Task(enabled: true), Author);
        var enabledYaml = File.ReadAllText(Path.Combine(_root, "config", "replications", "sales", "task.yaml"));

        _config.SaveReplicationTask(Task(enabled: false), Author);
        var disabledYaml = File.ReadAllText(Path.Combine(_root, "config", "replications", "sales", "task.yaml"));

        Assert.DoesNotContain("enabled", enabledYaml);
        Assert.Contains("enabled: false", disabledYaml);
    }
}
