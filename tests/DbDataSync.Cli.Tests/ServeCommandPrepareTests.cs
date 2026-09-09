using DbDataSync.Core.Config;
using LibGit2Sharp;

namespace DbDataSync.Cli.Tests;

/// <summary>
/// <c>ServeCommand.Prepare</c> writes a starter <c>dbdatasync.config.yaml</c> only for a repo root that
/// had no git repository before this run — see the phase 79 doc.
/// </summary>
public sealed class ServeCommandPrepareTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("dbdatasync-serve-prepare-tests-").FullName;

    // libgit2 marks objects it writes read-only, and plain Directory.Delete(recursive: true) refuses
    // to remove a read-only file on Windows — an environment-specific teardown wrinkle unrelated to
    // what each test actually asserts, so it is worked around here rather than left to fail teardown
    // after a passing test.
    public void Dispose() => GitTempDirectory.DeleteRecursively(_root);

    [Fact]
    public void FreshRoot_GetsAStarterFile_WithTheSecretRefComment()
    {
        ServeCommand.Prepare(_root);

        var path = DbDataSyncConfigFile.PathIn(_root);
        Assert.True(File.Exists(path));
        Assert.Contains("dbdatasync config secret set dbdatasync:config:stateConnectionString", File.ReadAllText(path));
    }

    [Fact]
    public void FreshRoot_CommitsTheStarterFile()
    {
        ServeCommand.Prepare(_root);

        using var repo = new Repository(_root);
        var head = repo.Head.Tip;
        Assert.NotNull(head);
        Assert.Contains("dbdatasync.config.yaml", head!.Tree.Select(e => e.Path));
    }

    [Fact]
    public void ExistingRepoRoot_SecondPrepareCall_LeavesTheFileUntouched()
    {
        ServeCommand.Prepare(_root);
        var path = DbDataSyncConfigFile.PathIn(_root);
        File.WriteAllText(path, "DbDataSync:\n  Url: http://operator-edited/\n");

        ServeCommand.Prepare(_root);

        Assert.Equal("http://operator-edited/", DbDataSyncConfigFile.Read(_root)["DbDataSync:Url"]);
    }

    [Fact]
    public void ARepoRootThatAlreadyHadGit_NeverGetsAStarterFile()
    {
        // A pre-existing git repository (not created by Prepare) that simply has no dbdatasync.config.yaml —
        // e.g. an operator who deleted it deliberately to configure entirely by flag/environment variable.
        Repository.Init(_root);

        ServeCommand.Prepare(_root);

        Assert.False(File.Exists(DbDataSyncConfigFile.PathIn(_root)));
    }
}
