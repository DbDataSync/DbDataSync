namespace DbDataSync.Cli.Tests;

/// <summary>
/// Every <c>dbdatasync</c> command resolves its repo root the same way — see <see cref="DbDataSyncRoot"/>.
/// Exercised over a real temp directory tree rather than mocked, since the whole point of the walk-up
/// is filesystem behaviour (parent directories, presence/absence of a file) that a mock would just
/// restate.
/// </summary>
public sealed class DbDataSyncRootTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("dbdatasync-root-tests-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public void ExplicitRepoFlag_WinsOutright()
    {
        var elsewhere = Path.Combine(_root, "elsewhere");
        Directory.CreateDirectory(elsewhere);
        File.WriteAllText(Path.Combine(_root, DbDataSync.Core.Config.DbDataSyncConfigFile.FileName), "");

        var resolved = DbDataSyncRoot.Resolve(["--repo", elsewhere], _root);

        Assert.Equal(elsewhere, resolved);
    }

    [Fact]
    public void ExplicitRepoFlag_ShortCircuitsEvenWhenAConfigFileExistsOnTheWayUp()
    {
        var deep = Path.Combine(_root, "a", "b", "c");
        Directory.CreateDirectory(deep);
        File.WriteAllText(Path.Combine(_root, DbDataSync.Core.Config.DbDataSyncConfigFile.FileName), "");

        var elsewhere = Path.Combine(_root, "elsewhere");
        var resolved = DbDataSyncRoot.Resolve(["--repo", elsewhere], deep);

        Assert.Equal(elsewhere, resolved);
    }

    [Fact]
    public void CwdHasTheFile_ResolvesToCwd()
    {
        File.WriteAllText(Path.Combine(_root, DbDataSync.Core.Config.DbDataSyncConfigFile.FileName), "");

        var resolved = DbDataSyncRoot.Resolve([], _root);

        Assert.Equal(_root, resolved);
    }

    [Fact]
    public void AParentSeveralLevelsUp_HasTheFile_WalksUpToFindIt()
    {
        File.WriteAllText(Path.Combine(_root, DbDataSync.Core.Config.DbDataSyncConfigFile.FileName), "");
        var deep = Path.Combine(_root, "a", "b", "c");
        Directory.CreateDirectory(deep);

        var resolved = DbDataSyncRoot.Resolve([], deep);

        Assert.Equal(_root, resolved);
    }

    [Fact]
    public void NeitherCwdNorAnyParent_HasTheFile_FallsBackToDefaultRoot()
    {
        var deep = Path.Combine(_root, "a", "b");
        Directory.CreateDirectory(deep);

        var resolved = DbDataSyncRoot.Resolve([], deep);

        Assert.Equal(CliOptions.DefaultRoot, resolved);
    }
}
