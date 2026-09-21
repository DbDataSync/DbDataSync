using DbDataSync.Core.Config;
using LibGit2Sharp;

namespace DbDataSync.Cli.Tests;

/// <summary>Phase 164's automatic rewrite of an existing <c>dbdatasync.config.yaml</c> from the old flat
/// key names to the new grouped ones — the common-case path any existing deployment actually hits, via
/// <see cref="ServeCommand.Prepare"/>.</summary>
public sealed class LegacyConfigMigrationTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("dbdatasync-legacy-config-migration-tests-").FullName;

    public LegacyConfigMigrationTests() => Repository.Init(_root);

    public void Dispose() => GitTempDirectory.DeleteRecursively(_root);

    [Fact]
    public void FreshStarterFile_MigratesNothing()
    {
        DbDataSyncConfigFile.WriteStarter(_root);

        var changes = LegacyConfigMigration.Migrate(_root);

        Assert.Empty(changes);
    }

    [Fact]
    public void PlainRenames_MoveTheValueUnchanged()
    {
        DbDataSyncConfigFile.SetValue(_root, "DbDataSync", "Url", "https://dbdatasync.example.com:5443");
        DbDataSyncConfigFile.SetValue(_root, "DbDataSync", "StateEngine", "Postgres");
        DbDataSyncConfigFile.SetValue(_root, "DbDataSync:Auth", "AdminGroup", "DbDataSync Admins");

        var changes = LegacyConfigMigration.Migrate(_root);

        Assert.NotEmpty(changes);
        var config = DbDataSyncConfigFile.Read(_root);
        Assert.Equal("https://dbdatasync.example.com:5443", config["DbDataSync:App:Url"]);
        Assert.Equal("Postgres", config["DbDataSync:State:Engine"]);
        Assert.Equal("DbDataSync Admins", config["DbDataSync:Auth:Windows:AdminGroup"]);
        Assert.False(config.ContainsKey("DbDataSync:Url"));
        Assert.False(config.ContainsKey("DbDataSync:StateEngine"));
        Assert.False(config.ContainsKey("DbDataSync:Auth:AdminGroup"));
    }

    [Fact]
    public void BareBooleans_BecomeModeStrings()
    {
        DbDataSyncConfigFile.SetValue(_root, "DbDataSync", "NotesRichMarkdown", "true");
        DbDataSyncConfigFile.SetValue(_root, "DbDataSync", "SelfUpdateEnabled", "false");
        DbDataSyncConfigFile.SetValue(_root, "DbDataSync", "NuGetSearchEnabled", "false");

        LegacyConfigMigration.Migrate(_root);

        var config = DbDataSyncConfigFile.Read(_root);
        Assert.Equal("rich", config["DbDataSync:Notes:MarkdownRenderer"]);
        Assert.Equal("disabled", config["DbDataSync:Updates:Mode"]);
        Assert.Equal("disabled", config["DbDataSync:Nuget:Search:Mode"]);
    }

    /// <summary>The one deliberate narrowing: the old flag trusted every request, from anywhere, as
    /// Admin; the new setting can only ever be loopback or disabled.</summary>
    [Fact]
    public void AuthDisabled_BecomesLoopbackAdminTrust_NotRemote()
    {
        DbDataSyncConfigFile.SetValue(_root, "DbDataSync:Auth", "Disabled", "true");

        var changes = LegacyConfigMigration.Migrate(_root);

        Assert.Equal("loopback", DbDataSyncConfigFile.Read(_root)["DbDataSync:Auth:Network:Admin"]);
        Assert.Contains(changes, c => c.Contains("Auth:Disabled") && c.Contains("Auth:Network:Admin"));
    }

    [Fact]
    public void PasskeyOrigins_MatchingUrl_IsDroppedWithNoAlternateUrls()
    {
        DbDataSyncConfigFile.SetValue(_root, "DbDataSync", "Url", "https://dbdatasync.example.com");
        DbDataSyncConfigFile.SetListValue(_root, "DbDataSync:Auth:Passkeys", "Origins", ["https://dbdatasync.example.com"]);

        LegacyConfigMigration.Migrate(_root);

        var config = DbDataSyncConfigFile.Read(_root);
        Assert.False(config.ContainsKey("DbDataSync:Auth:Passkeys:Origins:0"));
        Assert.False(config.ContainsKey("DbDataSync:App:AlternateUrls"));
        // The rest of the file survives the removal of a multi-line block cleanly.
        Assert.Equal("https://dbdatasync.example.com", config["DbDataSync:App:Url"]);
    }

    [Fact]
    public void PasskeyOrigins_WithAnExtraEntry_PreservesItAsAnAlternateUrl()
    {
        DbDataSyncConfigFile.SetValue(_root, "DbDataSync", "Url", "https://dbdatasync.example.com");
        DbDataSyncConfigFile.SetListValue(
            _root, "DbDataSync:Auth:Passkeys", "Origins",
            ["https://dbdatasync.example.com", "https://staging.dbdatasync.example.com"]);

        LegacyConfigMigration.Migrate(_root);

        var config = DbDataSyncConfigFile.Read(_root);
        Assert.Equal("https://staging.dbdatasync.example.com", config["DbDataSync:App:AlternateUrls"]);
        Assert.False(config.ContainsKey("DbDataSync:Auth:Passkeys:Origins:0"));
    }

    [Fact]
    public void RunningTwice_IsANoOpTheSecondTime()
    {
        DbDataSyncConfigFile.SetValue(_root, "DbDataSync", "Url", "http://localhost:5080");

        Assert.NotEmpty(LegacyConfigMigration.Migrate(_root));
        Assert.Empty(LegacyConfigMigration.Migrate(_root));
    }

    [Fact]
    public void ServeCommandPrepare_OnAnExistingLegacyRepo_MigratesAndCommits()
    {
        ServeCommand.Prepare(_root); // fresh: writes the (all-commented) starter, nothing to migrate
        DbDataSyncConfigFile.SetValue(_root, "DbDataSync", "Url", "https://dbdatasync.example.com");

        ServeCommand.Prepare(_root); // existing repo now: should migrate

        Assert.Equal("https://dbdatasync.example.com", DbDataSyncConfigFile.Read(_root)["DbDataSync:App:Url"]);
        using var repo = new Repository(_root);
        Assert.Contains(repo.Commits, c => c.MessageShort.Contains("Migrate dbdatasync.config.yaml"));
    }
}
