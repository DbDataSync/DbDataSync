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
        var (code, output) = await Run("set", "NotesRichMarkdown", "true", "--repo", _root);

        Assert.Equal(0, code);
        Assert.Equal("true", DbDataSyncConfigFile.Read(_root)["DbDataSync:NotesRichMarkdown"]);
        Assert.Contains("Restart the service", output);
        using var repo = new Repository(_root);
        Assert.Contains("Set 'DbDataSync:NotesRichMarkdown'", repo.Commits.First().Message);
    }

    [Fact]
    public async Task Set_WarnsBeforeEnablingARiskySetting_WithTheSameTextTheScreenShows()
    {
        var (_, output) = await Run("set", "NotesRichMarkdown", "true", "--repo", _root);

        Assert.Contains("Warning:", output);
        Assert.Contains(DbDataSync.Api.Services.AdminConfigService.Writable("NotesRichMarkdown")!.Caution!, output);
    }

    [Fact]
    public async Task Set_DoesNotWarnWhenTurningItBackOff()
    {
        await Run("set", "NotesRichMarkdown", "true", "--repo", _root);

        var (code, output) = await Run("set", "NotesRichMarkdown", "false", "--repo", _root);

        Assert.Equal(0, code);
        Assert.DoesNotContain("Warning:", output);
        Assert.Equal("false", DbDataSyncConfigFile.Read(_root)["DbDataSync:NotesRichMarkdown"]);
    }

    [Theory]
    [InlineData("TRUE", "true")]
    [InlineData("False", "false")]
    public async Task Set_NormalisesABooleanSpelling(string given, string written)
    {
        await Run("set", "DbDataSync:NotesRichMarkdown", given, "--repo", _root);

        Assert.Equal(written, DbDataSyncConfigFile.Read(_root)["DbDataSync:NotesRichMarkdown"]);
    }

    [Fact]
    public async Task Set_RefusesANonBooleanForAnOnOffSetting_AndWritesNothing()
    {
        var (code, output) = await Run("set", "NotesRichMarkdown", "yes please", "--repo", _root);

        Assert.Equal(1, code);
        Assert.Contains("true or false", output);
        Assert.False(DbDataSyncConfigFile.Read(_root).ContainsKey("DbDataSync:NotesRichMarkdown"));
    }

    [Theory]
    [InlineData("NoSuchSetting")]
    [InlineData("Auth:AdminGroup")]      // nested: the screen shows it but the writer cannot address it
    public async Task Set_RefusesAKeyTheAdminScreenWouldNotWrite(string key)
    {
        var (code, output) = await Run("set", key, "x", "--repo", _root);

        Assert.Equal(1, code);
        Assert.Contains("is not a setting this command can change", output);
        Assert.Contains("NotesRichMarkdown", output); // lists what it can
    }

    [Fact]
    public async Task Set_WithoutAConfiguration_SaysToRunSetupFirst_RatherThanCreatingOneFromNothing()
    {
        var elsewhere = Directory.CreateTempSubdirectory("dbdatasync-configvalue-empty-").FullName;
        try
        {
            var (code, output) = await Run("set", "NotesRichMarkdown", "true", "--repo", elsewhere);

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
        var (_, unset) = await Run("get", "NotesRichMarkdown", "--repo", _root);
        Assert.Contains("is not set", unset);
        Assert.Contains("default: false", unset);

        await Run("set", "NotesRichMarkdown", "true", "--repo", _root);
        var (_, set) = await Run("get", "NotesRichMarkdown", "--repo", _root);
        Assert.Contains("DbDataSync:NotesRichMarkdown = true", set);
    }

    [Fact]
    public async Task WrongArgumentCounts_PrintUsage()
    {
        Assert.Equal(1, (await Run("set", "NotesRichMarkdown", "--repo", _root)).ExitCode);
        Assert.Equal(1, (await Run("get", "--repo", _root)).ExitCode);
    }
}
