using DbDataSync.Core.Config;
using LibGit2Sharp;

namespace DbDataSync.Cli.Tests;

/// <summary><c>dbdatasync config get|set</c> — the CLI door onto the settings Admin Configuration and <c>setup</c> edit (phase 161).</summary>
public sealed class ConfigValueCommandTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("dbdatasync-configvalue-tests-").FullName;

    public ConfigValueCommandTests() => ServeCommand.Prepare(_root);

    public void Dispose() => GitTempDirectory.DeleteRecursively(_root);

    private static async Task<(int ExitCode, string Output)> Run(params string[] args)
    {
        var originalOut = Console.Out;
        var originalErr = Console.Error;
        using var output = new StringWriter();
        Console.SetOut(output);
        Console.SetError(output);
        try
        {
            return (await ConfigCommand.RunAsync(args), output.ToString());
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetError(originalErr);
        }
    }

    [Fact]
    public async Task Set_WritesTheKeyIntoTheConfigFile_AndCommitsIt()
    {
        var (code, output) = await Run("set", "Notes:MarkdownRenderer", "rich", "--repo", _root);

        Assert.Equal(0, code);
        Assert.Equal("rich", DbDataSyncConfigFile.Read(_root)["DbDataSync:Notes:MarkdownRenderer"]);
        Assert.Contains("Restart the service", output);
        using var repo = new Repository(_root);
        Assert.Contains("Set 'DbDataSync:Notes:MarkdownRenderer'", repo.Commits.First().Message);
    }

    [Fact]
    public async Task Set_WarnsBeforeEnablingARiskySetting_WithTheSameTextTheScreenShows()
    {
        var (_, output) = await Run("set", "Notes:MarkdownRenderer", "rich", "--repo", _root);

        Assert.Contains("Warning:", output);
        Assert.Contains(DbDataSync.Api.Services.AdminConfigService.Writable("Notes:MarkdownRenderer")!.Caution!, output);
    }

    [Fact]
    public async Task Set_DoesNotWarnWhenTurningItBackOff()
    {
        await Run("set", "Notes:MarkdownRenderer", "rich", "--repo", _root);

        var (code, output) = await Run("set", "Notes:MarkdownRenderer", "basic", "--repo", _root);

        Assert.Equal(0, code);
        Assert.DoesNotContain("Warning:", output);
        Assert.Equal("basic", DbDataSyncConfigFile.Read(_root)["DbDataSync:Notes:MarkdownRenderer"]);
    }

    /// <summary>
    /// Phase 164 removed every bare boolean from the catalog, so there is no writable key left whose
    /// DefaultValue is "true"/"false" — the boolean-specific spelling normalization/refusal
    /// <c>ConfigValueCommand.Set</c> used to have for that case was dead code once every setting became
    /// a mode string, and was removed along with it. Its replacement (AllowedValues) only applies to a
    /// mode string backed by a real, closed enum — an open-ended free-text setting like State:Engine
    /// (a custom dialect can be registered beyond the three built-ins) still takes whatever is given.
    /// </summary>
    [Fact]
    public async Task Set_WritesWhateverValueIsGiven_ForAnOpenEndedSetting()
    {
        var (code, output) = await Run("set", "State:Engine", "SomeCustomDialect", "--repo", _root);

        Assert.Equal(0, code);
        Assert.Equal("SomeCustomDialect", DbDataSyncConfigFile.Read(_root)["DbDataSync:State:Engine"]);
        Assert.DoesNotContain("true or false", output);
        Assert.DoesNotContain("must be one of", output);
    }

    /// <summary>A mode-string setting has a complete, closed set of legal values (derived from a real
    /// enum) — a value outside that set is refused rather than silently written.</summary>
    [Fact]
    public async Task Set_RefusesAValueNotInTheAllowedSet_ForAModeStringSetting()
    {
        var (code, output) = await Run("set", "Updates:Mode", "sometimes", "--repo", _root);

        Assert.Equal(1, code);
        Assert.Contains("must be one of: disabled, manual", output);
        Assert.False(DbDataSyncConfigFile.Read(_root).ContainsKey("DbDataSync:Updates:Mode"));
    }

    [Fact]
    public async Task Set_AcceptsAnAllowedValue_CaseInsensitively()
    {
        var (code, _) = await Run("set", "Updates:Mode", "MANUAL", "--repo", _root);

        Assert.Equal(0, code);
        Assert.Equal("MANUAL", DbDataSyncConfigFile.Read(_root)["DbDataSync:Updates:Mode"]);
    }

    [Theory]
    [InlineData("NoSuchSetting")]
    [InlineData("App:RepoRoot")]      // config-store root: how the file itself is found, never writable
    public async Task Set_RefusesAKeyTheAdminScreenWouldNotWrite(string key)
    {
        var (code, output) = await Run("set", key, "x", "--repo", _root);

        Assert.Equal(1, code);
        Assert.Contains("is not a setting this command can change", output);
        Assert.Contains("Notes:MarkdownRenderer", output); // lists what it can
    }

    [Fact]
    public async Task Set_WithoutAConfiguration_SaysToRunSetupFirst_RatherThanCreatingOneFromNothing()
    {
        var elsewhere = Directory.CreateTempSubdirectory("dbdatasync-configvalue-empty-").FullName;
        try
        {
            var (code, output) = await Run("set", "Notes:MarkdownRenderer", "rich", "--repo", elsewhere);

            Assert.Equal(1, code);
            Assert.Contains("dbdatasync setup", output);
            Assert.False(File.Exists(DbDataSyncConfigFile.PathIn(elsewhere)));
        }
        finally
        {
            GitTempDirectory.DeleteRecursively(elsewhere);
        }
    }

    [Fact]
    public async Task Get_ReportsWhatTheFileSays_OrThatItIsUnsetWithItsDefault()
    {
        var (_, unset) = await Run("get", "Notes:MarkdownRenderer", "--repo", _root);
        Assert.Contains("is not set", unset);
        Assert.Contains("default: basic", unset);

        await Run("set", "Notes:MarkdownRenderer", "rich", "--repo", _root);
        var (_, set) = await Run("get", "Notes:MarkdownRenderer", "--repo", _root);
        Assert.Contains("DbDataSync:Notes:MarkdownRenderer = rich", set);
    }

    [Fact]
    public async Task WrongArgumentCounts_PrintUsage()
    {
        Assert.Equal(1, (await Run("set", "Notes:MarkdownRenderer", "--repo", _root)).ExitCode);
        Assert.Equal(1, (await Run("get", "--repo", _root)).ExitCode);
    }
}
