using System.Text.Json;
using ClrKernel.Core.Secrets;
using DbDataSync.Core.Config;
using DbDataSync.Core.Git;
using DbDataSync.Core.Secrets;

namespace DbDataSync.Cli.Tests;

/// <summary>
/// <c>dbdatasync doctor</c>'s check list, run against real temp repos rather than mocked contexts —
/// the same reasoning <c>ServeCommandPrepareTests</c> uses. <see cref="BindingCheck"/> is left out of
/// every assertion here: none of these tests run a real DbDataSync host, so it always reports "did not
/// answer" regardless of the rest of the configuration, and asserting around it would only be
/// asserting that nothing is listening on port 5080.
/// </summary>
public sealed class DoctorCommandTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("dbdatasync-doctor-tests-").FullName;

    public void Dispose() => GitTempDirectory.DeleteRecursively(_root);

    [Fact]
    public async Task FreshSqliteRepo_RepoAndStateStoreAndAuthChecksPass()
    {
        ServeCommand.Prepare(_root);

        var context = DoctorCommand.BuildContext(["--repo", _root]);
        var results = await DoctorCommand.RunChecksAsync(context);

        Assert.Equal(CheckStatus.Ok, Find(results, "Repo").Status);
        Assert.Equal(CheckStatus.Ok, Find(results, "State store").Status);
        Assert.Equal(CheckStatus.Ok, Find(results, "Providers / drivers").Status);
        Assert.Equal(CheckStatus.Ok, Find(results, "Auth").Status);
    }

    [Fact]
    public async Task ConnectionNamingAnUninstalledDriver_ProvidersAndDriversCheckFails()
    {
        ServeCommand.Prepare(_root);
        var configRoot = Path.Combine(_root, "config");
        var repository = new ConfigRepository(
            configRoot, new GitCommitService(_root), new SecretStore("DbDataSync", true));
        repository.SaveConnection(
            new ConnectionInput
            {
                Name = "unreachable",
                DriverType = "NoSuchDriver",
                Host = "db.example",
                AuthMode = AuthMode.None,
            },
            CurrentUserForTests.Author);

        var context = DoctorCommand.BuildContext(["--repo", _root]);
        var results = await DoctorCommand.RunChecksAsync(context);

        var check = Find(results, "Providers / drivers");
        Assert.Equal(CheckStatus.Fail, check.Status);
        Assert.Contains("dbdatasync driver install", check.Detail);
    }

    [Fact]
    public async Task PasskeyRelyingPartyIdThatIsAUrl_AuthCheckFailsWithTheProblemText()
    {
        ServeCommand.Prepare(_root);
        DbDataSyncConfigFile.SetValue(_root, "DbDataSync:Auth:Passkeys", "RelyingPartyId", "https://example.com");

        var context = DoctorCommand.BuildContext(["--repo", _root]);
        var results = await DoctorCommand.RunChecksAsync(context);

        var check = Find(results, "Auth");
        Assert.Equal(CheckStatus.Fail, check.Status);
        Assert.Contains("looks like a URL", check.Detail);
    }

    [Fact]
    public async Task JsonOutput_IsValidAndNamesEveryCheck()
    {
        ServeCommand.Prepare(_root);

        var (exitCode, output) = RunDoctor(["--repo", _root, "--json"]);

        Assert.Equal(1, exitCode); // BindingCheck fails — nothing is listening.
        using var document = JsonDocument.Parse(output);
        var names = document.RootElement.EnumerateArray().Select(e => e.GetProperty("Name").GetString()).ToList();
        Assert.Contains("Repo", names);
        Assert.Contains("State store", names);
        Assert.Contains("Providers / drivers", names);
        Assert.Contains("Auth", names);
        Assert.Contains("Binding", names);
        Assert.Contains("First admin", names);
    }

    private static CheckResult Find(IReadOnlyList<CheckResult> results, string name) =>
        results.Single(r => r.Name == name);

    private static (int ExitCode, string Output) RunDoctor(string[] args)
    {
        var originalOut = Console.Out;
        using var output = new StringWriter();
        Console.SetOut(output);
        try
        {
            var exitCode = DoctorCommand.RunAsync(args).GetAwaiter().GetResult();
            return (exitCode, output.ToString());
        }
        finally
        {
            Console.SetOut(originalOut);
        }
    }
}

internal static class CurrentUserForTests
{
    public static GitAuthor Author { get; } = new("Test", "test@localhost");
}
