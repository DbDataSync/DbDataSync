using ClrKernel.Core.Secrets;
using DbDataSync.Core.Config;
using DbDataSync.Core.Git;
using LibGit2Sharp;

namespace DbDataSync.Core.Tests;

/// <summary>
/// Phase 35: acting on the config history that phase 6 has been showing since.
/// <para>
/// The history was always real and complete — every config write is an auto-commit with an author and
/// a message. What was missing was any way to act on it: an operator who broke a mapping five minutes
/// ago could see the commit that did it and had no way to look at it, let alone undo it.
/// </para>
/// <para>
/// Two properties carry most of the weight here. **A restore is a restore, not a `git revert`** — it
/// reads the tree at a commit rather than computing an inverse patch, so it cannot conflict and it
/// says what an operator means. And **history is never rewritten**: putting the config back adds a
/// commit rather than removing one, so the log shows the undo too and the undo is itself undoable.
/// </para>
/// </summary>
public sealed class ConfigHistoryDiffAndRestoreTests : IDisposable
{
    private static readonly GitAuthor Author = new("Test User", "test@example.com");

    private readonly string _repoRoot;
    private readonly string _configRoot;
    private readonly ConfigRepository _repository;

    public ConfigHistoryDiffAndRestoreTests()
    {
        _repoRoot = Directory.CreateTempSubdirectory("dbdatasync-history-tests-").FullName;
        Repository.Init(_repoRoot);
        _configRoot = Path.Combine(_repoRoot, "config");
        _repository = new ConfigRepository(
            _configRoot, new GitCommitService(_repoRoot), SecretStore.ForProviders([new InMemorySecretProvider()]));
    }

    public void Dispose() => GitTempDirectory.DeleteRecursively(_repoRoot);

    // ---- fixtures ---------------------------------------------------------------------------------

    private void SaveTask(string name) =>
        _repository.SaveReplicationTask(new ReplicationTaskConfig
        {
            Name = name,
            Scheduling = new SchedulingConfig { Mode = ScheduleMode.Continuous, FrequencySeconds = 30 },
            ChangeProcessing = new ChangeProcessingConfig
            {
                Reader = new ReaderConfig { Kind = "MsSqlChangeTracking" },
                Cache = new CacheConfig { Kind = "MsSqlStagingTable" },
                Writer = new WriterConfig { Kind = "MsSqlMerge" },
            },
        }, Author);

    private void SaveMapping(string replication, string mapping, string targetTable) =>
        _repository.SaveTableMapping(replication, new TableMappingConfig
        {
            Name = mapping,
            Sources = [new SourceTableSpec { ConnectionName = "orders-db", Database = "App", Table = "Orders" }],
            Targets = [new TableSpec { ConnectionName = "warehouse-db", Database = "DW", Table = targetTable }],
            ColumnMappings = [new ColumnMapping { SourceColumn = "Id", TargetColumn = "OrderId" }],
        }, Author);

    private string LatestSha() => _repository.GetReplicationHistory("crm-sync")[0].Sha;

    private int CommitCount()
    {
        using var repo = new Repository(_repoRoot);
        return repo.Commits.Count();
    }

    // ---- diff -------------------------------------------------------------------------------------

    /// <summary>What one commit changed, as a patch containing both sides — which is the whole point of
    /// a diff over the log line the tab already showed.</summary>
    [Fact]
    public void ACommitsDiff_CarriesBothVersionsOfWhatItChanged()
    {
        SaveTask("crm-sync");
        SaveMapping("crm-sync", "orders", "Orders");
        SaveMapping("crm-sync", "orders", "OrdersV2");

        var diff = _repository.GetReplicationCommitDiff("crm-sync", LatestSha());

        Assert.Equal(ConfigChangeKind.Modified, Assert.Single(diff.Changes).Kind);
        Assert.Equal("config/replications/crm-sync/table-mappings/orders.yaml", diff.Changes[0].Path);
        // Both sides, in full — which is what a diff editor renders from, and what a unified patch
        // would have elided most of for a file whose context is the point of reading it.
        Assert.Contains("table: Orders", diff.Changes[0].Before);
        Assert.Contains("table: OrdersV2", diff.Changes[0].After);
        Assert.False(diff.Truncated);
    }

    /// <summary>A replication's first commit has no parent, which is not an edge case to guard against
    /// but the ordinary way its creation appears — every file added.</summary>
    [Fact]
    public void TheFirstCommit_ReadsAsEveryFileAdded()
    {
        SaveTask("crm-sync");

        var diff = _repository.GetReplicationCommitDiff("crm-sync", LatestSha());

        var added = Assert.Single(diff.Changes);
        Assert.Equal(ConfigChangeKind.Added, added.Kind);
        Assert.Null(added.Before);
        Assert.Contains("name: crm-sync", added.After);
    }

    /// <summary>
    /// Scoped to this replication, so another one's changes in the same commit do not appear. A
    /// replication's history view showing a different replication's diff would be answering a question
    /// nobody asked.
    /// </summary>
    [Fact]
    public void ADiff_ShowsOnlyThisReplicationsFiles()
    {
        SaveTask("crm-sync");
        SaveTask("other-sync");

        // The most recent commit touched only other-sync, so crm-sync's view of it is empty rather
        // than wrong.
        using var repo = new Repository(_repoRoot);
        var diff = _repository.GetReplicationCommitDiff("crm-sync", repo.Head.Tip.Sha);

        Assert.Empty(diff.Changes);
    }

    [Fact]
    public void ADiffOfAShaThatIsNotACommit_SaysSoRatherThanFailingLater()
    {
        SaveTask("crm-sync");

        var ex = Assert.Throws<GitCommitNotFoundException>(
            () => _repository.GetReplicationCommitDiff("crm-sync", "0123456789abcdef0123456789abcdef01234567"));

        Assert.Contains("0123456789", ex.Message);
    }

    /// <summary>
    /// The distinction the whole restore confirmation rests on: a commit's own patch says what it
    /// changed, and the restore preview says what restoring to it would change. Once anything has
    /// happened since, those are different sets — here, restoring to a commit that only added `orders`
    /// would also delete the `invoices` mapping created after it, which is nowhere in that commit's own
    /// patch.
    /// </summary>
    [Fact]
    public void TheRestorePreview_IsNotTheCommitsOwnPatch()
    {
        SaveTask("crm-sync");
        SaveMapping("crm-sync", "orders", "Orders");
        var afterOrders = LatestSha();
        SaveMapping("crm-sync", "invoices", "Invoices");

        var commitDiff = _repository.GetReplicationCommitDiff("crm-sync", afterOrders);
        Assert.Equal(ConfigChangeKind.Added, Assert.Single(commitDiff.Changes).Kind);
        Assert.EndsWith("orders.yaml", commitDiff.Changes[0].Path);

        var preview = _repository.GetReplicationRestoreDiff("crm-sync", afterOrders);
        var change = Assert.Single(preview.Changes);
        Assert.Equal(ConfigChangeKind.Deleted, change.Kind);
        Assert.EndsWith("invoices.yaml", change.Path);
    }

    // ---- restore ----------------------------------------------------------------------------------

    /// <summary>
    /// The shape of the whole feature: the earlier content comes back, and it comes back as a **new**
    /// commit with the later one still in the log. History is never rewritten.
    /// </summary>
    [Fact]
    public void ARestore_BringsBackTheOlderContentAsANewCommit()
    {
        SaveTask("crm-sync");
        SaveMapping("crm-sync", "orders", "Orders");
        var original = LatestSha();
        SaveMapping("crm-sync", "orders", "OrdersV2");
        Assert.Equal("OrdersV2", _repository.LoadTableMapping("crm-sync", "orders").Targets[0].Table);

        var before = CommitCount();
        var result = _repository.RestoreReplication("crm-sync", original, Author);

        Assert.Equal("Orders", _repository.LoadTableMapping("crm-sync", "orders").Targets[0].Table);
        Assert.Equal(before + 1, CommitCount());
        Assert.NotNull(result.CommitSha);
        Assert.Equal(original, result.RestoredFromSha);

        // The log grew rather than shrank, and the commit it grew by says what it was.
        var history = _repository.GetReplicationHistory("crm-sync");
        Assert.Contains(history, c => c.Sha == original);
        Assert.StartsWith($"Restore replication 'crm-sync' to {original[..8]}", history[0].Message);
    }

    /// <summary>A mapping created after the restore point is removed, because a restore is the tree at
    /// that commit rather than a patch applied to this one. Leaving it would produce neither the old
    /// state nor the new one.</summary>
    [Fact]
    public void ARestore_DeletesAMappingCreatedAfterThatCommit()
    {
        SaveTask("crm-sync");
        SaveMapping("crm-sync", "orders", "Orders");
        var beforeInvoices = LatestSha();
        SaveMapping("crm-sync", "invoices", "Invoices");
        Assert.Equal(["invoices", "orders"], _repository.ListTableMappings("crm-sync"));

        var result = _repository.RestoreReplication("crm-sync", beforeInvoices, Author);

        Assert.Equal(["orders"], _repository.ListTableMappings("crm-sync"));
        Assert.False(File.Exists(
            Path.Combine(_configRoot, "replications", "crm-sync", "table-mappings", "invoices.yaml")));
        Assert.Equal(ConfigChangeKind.Deleted, Assert.Single(result.Changes).Kind);
    }

    /// <summary>A restore is undoable, because it is an ordinary commit like any other — which is the
    /// practical payoff of never rewriting history.</summary>
    [Fact]
    public void ARestore_IsItselfRestorable()
    {
        SaveTask("crm-sync");
        SaveMapping("crm-sync", "orders", "Orders");
        var first = LatestSha();
        SaveMapping("crm-sync", "orders", "OrdersV2");
        var second = LatestSha();

        _repository.RestoreReplication("crm-sync", first, Author);
        Assert.Equal("Orders", _repository.LoadTableMapping("crm-sync", "orders").Targets[0].Table);

        _repository.RestoreReplication("crm-sync", second, Author);
        Assert.Equal("OrdersV2", _repository.LoadTableMapping("crm-sync", "orders").Targets[0].Table);
    }

    /// <summary>
    /// Restoring to a commit from before the replication existed would mean deleting it, and that is
    /// what the Delete action is for. Refusing says so rather than silently emptying the directory.
    /// </summary>
    [Fact]
    public void ARestoreToBeforeTheReplicationExisted_IsRefused()
    {
        SaveTask("other-sync");
        using var repo = new Repository(_repoRoot);
        var beforeCrm = repo.Head.Tip.Sha;
        SaveTask("crm-sync");

        var ex = Assert.Throws<ConfigValidationException>(
            () => _repository.RestoreReplication("crm-sync", beforeCrm, Author));

        Assert.Contains("did not exist at commit", ex.Message);
        // And it is still there: a refusal leaves the working tree untouched.
        Assert.Equal("crm-sync", _repository.LoadReplicationTask("crm-sync").Name);
    }

    /// <summary>
    /// The guarantee that makes this safe to offer: a restore that would produce config this tool would
    /// reject on save is refused with what would have broken, and refusing writes nothing. Here the
    /// restored mapping has no resolvable target endpoint — the same thing a save rejects — so the
    /// restore rejects it too rather than reaching the same state through a different door.
    /// </summary>
    [Fact]
    public void ARestoreProducingConfigASaveWouldReject_IsRefusedAndWritesNothing()
    {
        SaveTask("crm-sync");
        SaveMapping("crm-sync", "orders", "Orders");
        var good = LatestSha();

        // A mapping written straight to disk and committed, bypassing SaveTableMapping's validation —
        // which is how a hand-edited config repo, or one restored from elsewhere, can carry one.
        var path = Path.Combine(_configRoot, "replications", "crm-sync", "table-mappings", "broken.yaml");
        // Written as literal YAML rather than through the serializer, which is internal — and which
        // is the more honest fixture anyway: this is what a hand-edited file looks like.
        File.WriteAllText(path, """
            name: broken
            sources:
            - connectionName: orders-db
              database: App
              table: Orders
            targets:
            - table: NoConnectionAnywhere
            columnMappings:
            - sourceColumn: Id
              targetColumn: OrderId
            """);
        new GitCommitService(_repoRoot).CommitChanges([path], "Hand-edited broken mapping", Author);
        var broken = LatestSha();

        // Restoring *forward* onto the broken commit is what must be refused.
        _repository.RestoreReplication("crm-sync", good, Author);
        var ex = Assert.Throws<ConfigValidationException>(
            () => _repository.RestoreReplication("crm-sync", broken, Author));

        Assert.Contains("target", ex.Message);
        Assert.False(File.Exists(path), "a refused restore must not have written any of the set it refused");
        Assert.Equal(["orders"], _repository.ListTableMappings("crm-sync"));
    }

    /// <summary>
    /// A connection the restored config names but which no longer exists is a **warning**, not a
    /// refusal — saving a mapping that names a connection which does not exist is allowed today, so
    /// refusing here would make the restore stricter than the save it restores. It is still the most
    /// likely way a restore lands a replication that cannot run, so it is said out loud.
    /// </summary>
    [Fact]
    public void ARestoreNamingAMissingConnection_WarnsRatherThanRefusing()
    {
        SaveTask("crm-sync");
        SaveMapping("crm-sync", "orders", "Orders");
        var sha = LatestSha();
        SaveMapping("crm-sync", "orders", "OrdersV2");

        var result = _repository.RestoreReplication("crm-sync", sha, Author);

        Assert.Equal("Orders", _repository.LoadTableMapping("crm-sync", "orders").Targets[0].Table);
        Assert.Contains(result.Warnings, w => w.Contains("orders-db"));
        Assert.Contains(result.Warnings, w => w.Contains("warehouse-db"));
    }

    /// <summary>A connection that does exist is not warned about, so the warning means something when
    /// it appears.</summary>
    [Fact]
    public void ARestoreWhoseConnectionsAllExist_WarnsAboutNothing()
    {
        _repository.SaveConnection(new ConnectionInput
        {
            Name = "orders-db", DriverType = DriverIds.MsSql, Host = "localhost", AuthMode = AuthMode.IntegratedAuth,
        }, Author);
        _repository.SaveConnection(new ConnectionInput
        {
            Name = "warehouse-db", DriverType = DriverIds.MsSql, Host = "localhost", AuthMode = AuthMode.IntegratedAuth,
        }, Author);
        SaveTask("crm-sync");
        SaveMapping("crm-sync", "orders", "Orders");
        var sha = LatestSha();
        SaveMapping("crm-sync", "orders", "OrdersV2");

        Assert.Empty(_repository.RestoreReplication("crm-sync", sha, Author).Warnings);
    }

    /// <summary>Restoring to where the config already is records nothing — an empty commit would be a
    /// log entry claiming a change that did not happen.</summary>
    [Fact]
    public void ARestoreToTheCurrentState_RecordsNoCommit()
    {
        SaveTask("crm-sync");
        SaveMapping("crm-sync", "orders", "Orders");
        var sha = LatestSha();

        var before = CommitCount();
        var result = _repository.RestoreReplication("crm-sync", sha, Author);

        Assert.Null(result.CommitSha);
        Assert.Equal(before, CommitCount());
    }

    /// <summary>
    /// Phase 81 left this open and phase 35 was named as the phase that would take it: the repo-root
    /// `dbdatasync.config.yaml` is git-tracked and was invisible to every query here, because a
    /// directory-prefix match only ever matches paths *below* the prefix and a bare file has none.
    /// </summary>
    [Fact]
    public void ARepoRootFile_HasHistoryAndADiffLikeAnythingElse()
    {
        var path = Path.Combine(_repoRoot, "dbdatasync.config.yaml");
        var git = new GitCommitService(_repoRoot);
        File.WriteAllText(path, "port: 5001\n");
        git.CommitChanges([path], "Set the console port", Author);

        var history = git.GetHistory("dbdatasync.config.yaml");
        Assert.Equal("Set the console port", Assert.Single(history).Message);

        var diff = git.GetCommitDiff("dbdatasync.config.yaml", history[0].Sha);
        Assert.Equal("dbdatasync.config.yaml", Assert.Single(diff.Changes).Path);
        Assert.Contains("port: 5001", diff.Changes[0].After);
    }
}
