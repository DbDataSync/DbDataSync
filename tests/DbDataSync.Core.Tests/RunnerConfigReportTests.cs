using ClrKernel.Core.Secrets;
using DbDataSync.Core.Config;
using DbDataSync.Core.Git;
using LibGit2Sharp;

namespace DbDataSync.Core.Tests;

/// <summary>
/// The one config write a run is allowed to make — phase 94's <see cref="IRunnerConfig"/>, here in the
/// owning process's own implementation.
/// <para>
/// The behaviour these tests pin is what makes the write safe to be automated: it touches one field of
/// the *current* file rather than writing back a mapping as old as the pass that reported it, and it
/// commits nothing when it has nothing to say. The second is what lets a runner report on every
/// provisioning pass instead of only the one that ran DDL — which is, in turn, why a lost report needs
/// no journal.
/// </para>
/// </summary>
public sealed class RunnerConfigReportTests : IDisposable
{
    private static readonly GitAuthor Author = new("DbDataSync", "dbdatasync@localhost");

    private readonly string _repoRoot = Directory.CreateTempSubdirectory("dbdatasync-runner-config-").FullName;
    private readonly ConfigRepository _config;
    private readonly LocalRunnerConfig _reports;

    public RunnerConfigReportTests()
    {
        Repository.Init(_repoRoot);
        _config = new ConfigRepository(
            Path.Combine(_repoRoot, "config"), new GitCommitService(_repoRoot),
            SecretStore.ForProviders([new InMemorySecretProvider()]));
        _reports = new LocalRunnerConfig(_config, Author);

        _config.SaveReplicationTask(new ReplicationTaskConfig
        {
            Name = "r",
            Scheduling = new SchedulingConfig { Mode = ScheduleMode.Periodic },
            ChangeProcessing = new ChangeProcessingConfig
            {
                Reader = new ReaderConfig { Kind = "BatchReload" },
                Cache = new CacheConfig { Kind = "StagingTable" },
                Writer = new WriterConfig { Kind = "DeleteInsert" },
            },
        }, Author);

        SaveMapping();
    }

    public void Dispose() => GitTempDirectory.DeleteRecursively(_repoRoot);

    private void SaveMapping(string notes = "as created") =>
        _config.SaveTableMapping("r", new TableMappingConfig
        {
            Name = "m",
            Sources = [new SourceTableSpec { ConnectionName = "src", Database = "App", Schema = "dbo", Table = "Orders" }],
            Targets = [new TableSpec { ConnectionName = "tgt", Database = "DW", Schema = "dbo", Table = "Orders" }],
            ColumnMappings = [new ColumnMapping { SourceColumn = "Id", TargetColumn = "Id" }],
            Notes = notes,
        }, Author);

    private static CachedColumn[] Provisioned() =>
    [
        new("Id", "int", false, true, false),
        new("Region", "nvarchar(50)", true, false, false),
    ];

    private string HeadMessage()
    {
        using var repo = new Repository(_repoRoot);
        return repo.Head.Tip.Message;
    }

    private int CommitCount()
    {
        using var repo = new Repository(_repoRoot);
        return repo.Commits.Count();
    }

    [Fact]
    public void A_report_lands_in_the_mappings_cache_on_disk()
    {
        _reports.ReportProvisionedTargetColumns("r", "m", Provisioned());

        var stored = _config.LoadTableMapping("r", "m");
        Assert.Equal(["Id", "Region"], stored.TargetColumns.Select(c => c.Name));
        Assert.Equal("nvarchar(50)", stored.TargetColumns[1].NativeType);
        Assert.True(stored.TargetColumns[0].IsPrimaryKey);

        // A side really was read, so the age an operator judges staleness by moves — the same rule a
        // Refresh follows.
        Assert.NotNull(stored.ColumnsCapturedUtc);
    }

    [Fact]
    public void A_report_is_committed_under_the_identity_it_was_given()
    {
        _reports.ReportProvisionedTargetColumns("r", "m", Provisioned());

        using var repo = new Repository(_repoRoot);
        Assert.Equal(Author.Name, repo.Head.Tip.Author.Name);
        Assert.Equal(Author.Email, repo.Head.Tip.Author.Email);
        Assert.Contains("table mapping 'm'", HeadMessage());
    }

    /// <summary>
    /// The property that makes a stale in-flight mapping harmless. A runner's copy is as old as the
    /// start of its pass; an operator can have edited the mapping since. Writing that copy back would
    /// revert the edit, so only the one field this report is about is applied.
    /// </summary>
    [Fact]
    public void A_report_does_not_revert_an_edit_made_while_the_pass_was_running()
    {
        SaveMapping(notes: "edited mid-pass");

        _reports.ReportProvisionedTargetColumns("r", "m", Provisioned());

        var stored = _config.LoadTableMapping("r", "m");
        Assert.Equal("edited mid-pass", stored.Notes);
        Assert.Equal(2, stored.TargetColumns.Count);
    }

    /// <summary>
    /// Why the report can safely fire on every auto-provisioning pass rather than only the DDL one —
    /// and so why losing one costs a pass rather than needing a journal to prevent.
    /// </summary>
    [Fact]
    public void A_repeated_report_of_the_same_shape_commits_nothing()
    {
        _reports.ReportProvisionedTargetColumns("r", "m", Provisioned());
        var afterFirst = CommitCount();
        var captured = _config.LoadTableMapping("r", "m").ColumnsCapturedUtc;

        _reports.ReportProvisionedTargetColumns("r", "m", Provisioned());

        Assert.Equal(afterFirst, CommitCount());
        // Not restamped either: nothing about the picture changed, so its age should not reset.
        Assert.Equal(captured, _config.LoadTableMapping("r", "m").ColumnsCapturedUtc);
    }

    [Fact]
    public void A_report_of_a_changed_shape_overwrites_the_cache()
    {
        _reports.ReportProvisionedTargetColumns("r", "m", Provisioned());

        _reports.ReportProvisionedTargetColumns("r", "m",
        [
            new CachedColumn("Id", "int", false, true, false),
            new CachedColumn("Region", "nvarchar(50)", true, false, false),
            new CachedColumn("Added", "bit", true, false, false),
        ]);

        Assert.Equal(["Id", "Region", "Added"], _config.LoadTableMapping("r", "m").TargetColumns.Select(c => c.Name));
    }
}
