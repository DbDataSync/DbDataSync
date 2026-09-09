using DbDataSync.Core.Config;

namespace DbDataSync.Cli.Tests;

/// <summary>
/// Phase 112 moved <see cref="CliOptions.DefaultRoot"/> from a per-user location to a machine-wide
/// one — this is the check that keeps an upgrade from silently looking empty. Driven through the
/// three-argument overload of <see cref="LegacyRootMigration.DetectAt(string, string, string)"/> with
/// fake temp directories, never the real OS-defined default/legacy paths.
/// </summary>
public sealed class LegacyRootMigrationTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("dbdatasync-legacy-migration-tests-").FullName;
    private readonly string _defaultRoot;
    private readonly string _legacyRoot;

    public LegacyRootMigrationTests()
    {
        _defaultRoot = Path.Combine(_root, "new-default");
        _legacyRoot = Path.Combine(_root, "old-per-user");
    }

    public void Dispose() => GitTempDirectory.DeleteRecursively(_root);

    [Fact]
    public void ResolvedRootIsNotTheDefault_NeverFires()
    {
        CreateRealConfigAt(_legacyRoot);

        var detected = LegacyRootMigration.DetectAt(
            resolvedRoot: Path.Combine(_root, "explicit-repo"), _defaultRoot, _legacyRoot);

        Assert.Null(detected);
    }

    [Fact]
    public void DefaultAlreadyHasAConfiguration_DoesNotFire()
    {
        CreateRealConfigAt(_defaultRoot);
        CreateRealConfigAt(_legacyRoot);

        var detected = LegacyRootMigration.DetectAt(_defaultRoot, _defaultRoot, _legacyRoot);

        Assert.Null(detected);
    }

    [Fact]
    public void LegacyLocationHasNoConfiguration_DoesNotFire()
    {
        var detected = LegacyRootMigration.DetectAt(_defaultRoot, _defaultRoot, _legacyRoot);

        Assert.Null(detected);
    }

    [Fact]
    public void DefaultIsEmptyAndLegacyHasARealConfiguration_Fires()
    {
        CreateRealConfigAt(_legacyRoot);

        var detected = LegacyRootMigration.DetectAt(_defaultRoot, _defaultRoot, _legacyRoot);

        Assert.Equal(_legacyRoot, detected);
    }

    [Fact]
    public void Message_NamesBothPaths()
    {
        var message = LegacyRootMigration.Message(_legacyRoot, _defaultRoot);

        Assert.Contains(_legacyRoot, message);
        Assert.Contains(_defaultRoot, message);
        Assert.Contains("dbdatasync serve --repo", message);
    }

    /// <summary><see cref="ServeCommand.Prepare"/> alone only writes a fully-commented starter file —
    /// zero live keys, so <see cref="ExistingSetup.DetectedAt"/> would still say "nothing here." A
    /// real configuration needs at least one uncommented key.</summary>
    private static void CreateRealConfigAt(string root)
    {
        ServeCommand.Prepare(root);
        DbDataSyncConfigFile.SetValue(root, "DbDataSync", "Url", "http://localhost:5080");
    }
}
