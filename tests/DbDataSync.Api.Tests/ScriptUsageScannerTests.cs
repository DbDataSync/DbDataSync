using ClrKernel.Core.Secrets;
using DbDataSync.Api.Services;
using DbDataSync.Core.Config;
using DbDataSync.Core.Git;
using LibGit2Sharp;

namespace DbDataSync.Api.Tests;

/// <summary>
/// The first question anyone has about a script they did not write: is it bound to anything?
/// </summary>
public sealed class ScriptUsageScannerTests : IDisposable
{
    private static readonly GitAuthor Author = new("Test", "test@example.com");

    private readonly string _repoRoot = Directory.CreateTempSubdirectory("dbdatasync-usage-scan-").FullName;
    private readonly ConfigRepository _config;
    private readonly ScriptUsageScanner _scanner;

    public ScriptUsageScannerTests()
    {
        Repository.Init(_repoRoot);
        _config = new ConfigRepository(
            Path.Combine(_repoRoot, "config"), new GitCommitService(_repoRoot),
            SecretStore.ForProviders([new InMemorySecretProvider()]));
        _scanner = new ScriptUsageScanner(_config);
    }

    public void Dispose() => Directory.Delete(_repoRoot, recursive: true);

    private void SaveConnection(
        string name,
        Dictionary<string, ScriptBinding?>? scripts = null,
        Dictionary<string, List<HookConfig>?>? hooks = null) =>
        _config.SaveConnection(new ConnectionInput
        {
            Name = name,
            DriverType = ConnectionDriverType.MsSql,
            Host = "localhost",
            Database = "App",
            AuthMode = AuthMode.IntegratedAuth,
            Scripts = scripts ?? [],
            Hooks = hooks ?? [],
        }, Author);

    private void SaveReplication(string name, Dictionary<string, ScriptBinding?>? scripts = null) =>
        _config.SaveReplicationTask(new ReplicationTaskConfig
        {
            Name = name,
            Scheduling = new SchedulingConfig { Mode = ScheduleMode.Continuous, FrequencySeconds = 30 },
            ChangeProcessing = new ChangeProcessingConfig
            {
                Reader = new ReaderConfig { Kind = "MsSqlChangeTracking" },
                Cache = new CacheConfig { Kind = "MsSqlStagingTable" },
                Writer = new WriterConfig { Kind = "MsSqlMerge" },
            },
            Scripts = scripts ?? [],
        }, Author);

    private void SaveMapping(string replicationName, string name, Dictionary<string, ScriptBinding?> scripts) =>
        _config.SaveTableMapping(replicationName, new TableMappingConfig
        {
            Name = name,
            Sources = [new SourceTableSpec { ConnectionName = "src", Database = "App", Table = "Orders" }],
            Targets = [new TableSpec { ConnectionName = "src", Database = "App", Table = "Orders" }],
            ColumnMappings = [new ColumnMapping { SourceColumn = "Id", TargetColumn = "Id" }],
            Scripts = scripts,
        }, Author);

    /// <summary>A hook binding is validated at save against the script it names, so the script has to
    /// exist for the binding to be saveable at all.</summary>
    private void SaveSqlHook(string name) =>
        _config.SaveScript(new ScriptDefinition
        {
            Manifest = new ScriptConfig { Name = name, Kind = "hook", Language = ScriptLanguage.Sql },
            Code = "TRUNCATE TABLE {{target}};",
        }, Author);

    [Fact]
    public void An_empty_store_reports_nothing()
    {
        Assert.Empty(_scanner.ScanAll());
    }

    [Fact]
    public void A_script_is_reported_at_every_level_that_binds_it()
    {
        SaveConnection("src", scripts: new() { ["metadataProvider"] = new ScriptBinding { ScriptName = "catalog" } });
        SaveReplication("sales", scripts: new() { ["rowTransform"] = new ScriptBinding { ScriptName = "shared" } });
        SaveMapping("sales", "orders", new() { ["sqlColumnExpression"] = new ScriptBinding { ScriptName = "shared" } });

        var usages = _scanner.ScanAll();

        Assert.Equal(["catalog", "shared"], usages.Keys.Order());
        Assert.Equal(new ScriptUsage("connection", "src", "metadataProvider"), Assert.Single(usages["catalog"]));

        Assert.Equal(
            [new ScriptUsage("replication", "sales", "rowTransform"),
             new ScriptUsage("mapping", "sales / orders", "sqlColumnExpression")],
            usages["shared"].OrderBy(u => u.Level == "replication" ? 0 : 1));
    }

    /// <summary>
    /// Hooks bind by name from a point's list rather than through the slot hierarchy, so scanning
    /// <c>Scripts</c> alone would report a reusable SQL hook in daily use as unused — which is exactly
    /// the wrong answer for the one thing this column exists to say.
    /// </summary>
    [Fact]
    public void A_hook_bound_by_name_counts_as_used()
    {
        SaveSqlHook("truncate-staging");
        SaveConnection("src", hooks: new()
        {
            ["beforeStage"] = [new HookConfig { Hook = "truncate-staging" }],
        });

        var usage = Assert.Single(_scanner.ScanAll()["truncate-staging"]);

        Assert.Equal("connection", usage.Level);
        Assert.Equal("beforeStage", usage.Slot);
    }

    /// <summary>An inline-SQL hook names no script, and a slot bound to "explicitly none" uses none
    /// either — both are real configuration and neither is a use of anything.</summary>
    [Fact]
    public void A_binding_that_names_no_script_is_not_a_use()
    {
        SaveConnection("src",
            scripts: new() { ["rowTransform"] = null },
            hooks: new() { ["beforeStage"] = [new HookConfig { Sql = "TRUNCATE TABLE staging;" }] });

        Assert.Empty(_scanner.ScanAll());
    }
}
