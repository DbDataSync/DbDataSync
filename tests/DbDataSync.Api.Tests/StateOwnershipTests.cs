using DbDataSync.Api.Configuration;
using DbDataSync.Api.Services;
using DbDataSync.State.Remote;

namespace DbDataSync.Api.Tests;

/// <summary>
/// Phase 39's two requirements, asserted as requirements rather than through their consequences: one
/// process opens the state file, and the token it hands its children is never on a command line.
/// Both are the kind of property a later refactor undoes silently while every behavioural test stays
/// green.
/// </summary>
public sealed class StateOwnershipTests
{
    /// <summary>
    /// Constructing a <c>StateDatabase</c> is opening the file. Only the project that defines it and
    /// the process that owns it may do that — the TaskRunner reaches state over loopback instead, and
    /// tests build their own fixtures.
    /// </summary>
    [Fact]
    public void Only_the_api_opens_the_state_file()
    {
        var offenders = Directory
            .EnumerateFiles(Path.Combine(RepoRoot, "src"), "*.cs", SearchOption.AllDirectories)
            .Where(file => File.ReadAllText(file).Contains("new StateDatabase("))
            .Select(file => Path.GetRelativePath(RepoRoot, file))
            .Order()
            .ToList();

        // Phase 79 centralized this further: DbDataSyncHost.cs and InviteCommand.cs used to construct a
        // StateDatabase directly (the API's composition root, and one named exception — see below for
        // why InviteCommand gets one), and now both go through StateDatabase.FromOptions instead, so
        // there is exactly one legitimate construction site rather than two.
        //
        // The rule exists because *runner processes* are spawned constantly and concurrently, and many
        // writers against one SQLite file is what phase 39 was fixing; they reach state over loopback
        // instead. `dbdatasync invite` is a different risk profile: an operator runs it once, by hand,
        // in the situation where nobody can sign in — which is precisely when an endpoint that needs a
        // session is no help. One occasional writer alongside the API is what SQLite's busy_timeout
        // and SqliteRetry already handle everywhere else in this codebase. FromOptions is what both of
        // them call now, so the invariant holds without either of them needing an exception of its own.
        //
        // Listed by name rather than by relaxing the rule, so the next file that wants an exception
        // has to argue for it here.
        Assert.Equal(
            [
                Path.Combine("src", "DbDataSync.State", "StateDatabase.Factory.cs"),
            ],
            offenders);
    }

    /// <summary>
    /// On Linux a process's command line is world-readable (<c>/proc/&lt;pid&gt;/cmdline</c>) and its
    /// environment is not (<c>/proc/&lt;pid&gt;/environ</c>), so a token in an argument would be
    /// visible to every local user through <c>ps</c> — precisely the threat it exists to answer.
    /// </summary>
    [Fact]
    public void The_runner_token_reaches_a_child_through_the_environment_and_not_its_arguments()
    {
        const string token = "a-secret-that-must-not-appear-in-ps";
        var options = new ApiOptions
        {
            RepoRoot = "/tmp/repo",
            StateDbPath = "/tmp/repo/state.db",
            TaskRunnerDllPath = "/tmp/DbDataSync.TaskRunner.dll",
        };

        var startInfo = ProcessSupervisor.BuildStartInfo(options, "sales", "http://127.0.0.1:5891", token);

        Assert.DoesNotContain(token, startInfo.ArgumentList);
        Assert.DoesNotContain(token, startInfo.Arguments);
        Assert.Equal(token, startInfo.Environment[StateProtocol.TokenEnvironmentVariable]);
        Assert.Equal("http://127.0.0.1:5891", startInfo.Environment[StateProtocol.EndpointEnvironmentVariable]);

        // Environment is only honoured with UseShellExecute false, so the two go together.
        Assert.False(startInfo.UseShellExecute);
    }

    private static string RepoRoot
    {
        get
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "DbDataSync.slnx")))
                directory = directory.Parent;
            return directory?.FullName
                ?? throw new InvalidOperationException("Could not locate the repo root (DbDataSync.slnx).");
        }
    }
}
