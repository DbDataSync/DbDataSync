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
        File.WriteAllText(path, "DbDataSync:App:\n  Url: http://operator-edited/\n");

        ServeCommand.Prepare(_root);

        Assert.Equal("http://operator-edited/", DbDataSyncConfigFile.Read(_root)["DbDataSync:App:Url"]);
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

    /// <summary>Phase 135 — no service ever registered against this directory, so the plain exception
    /// message is all there is to say.</summary>
    [Fact]
    public void PrepareFailureMessage_NoServiceRegistered_IsJustTheExceptionMessage()
    {
        var message = ServeCommand.PrepareFailureMessage(_root, new IOException("disk full"));

        Assert.Equal($"Could not prepare the config repository at '{_root}': disk full", message);
    }

    /// <summary>Phase 135's own motivating scenario: an ownership failure, with a service registration
    /// on record — the message should name the account/platform it was set up for.</summary>
    [Fact]
    public void PrepareFailureMessage_ServiceRegistered_AppendsTheAccountAndPlatform()
    {
        ServiceRegistration.Write(_root, "LocalSystem", "windows");

        var message = ServeCommand.PrepareFailureMessage(_root, new IOException("not owned by current user"));

        Assert.Contains("not owned by current user", message);
        Assert.Contains("registered for the 'LocalSystem' windows service account", message);
    }

    /// <summary>
    /// Phase 136's dispatch helper, exercised on this real (non-Windows) sandbox — the
    /// <c>WindowsServiceEventLog</c> half of its behavior can only be proven on a real Windows box (see
    /// <c>WindowsServiceEventLogTests</c>, Windows-only), but the "not running as a Windows service"
    /// branch is exactly what every non-Windows CI run and every interactive terminal on any platform
    /// actually exercises, and is real, runnable coverage of the restructuring itself — not skipped here.
    /// </summary>
    [Fact]
    public void Fail_NotAWindowsService_WritesTheGivenMessageToConsoleErrorAndReturnsOne()
    {
        var originalErr = Console.Error;
        using var output = new StringWriter();
        Console.SetError(output);
        try
        {
            var exitCode = ServeCommand.Fail(new InvalidOperationException("unused"), "a specific message");

            Assert.Equal(1, exitCode);
            Assert.Equal($"a specific message{Environment.NewLine}", output.ToString());
        }
        finally
        {
            Console.SetError(originalErr);
        }
    }

    /// <summary>No message given falls back to the exception's own type and text, identically on both
    /// the service and non-service paths.</summary>
    [Fact]
    public void Fail_NoMessageGiven_FallsBackToTheExceptionTypeAndMessage()
    {
        var originalErr = Console.Error;
        using var output = new StringWriter();
        Console.SetError(output);
        try
        {
            var exitCode = ServeCommand.Fail(new InvalidOperationException("boom"), message: null);

            Assert.Equal(1, exitCode);
            Assert.Equal($"InvalidOperationException: boom{Environment.NewLine}", output.ToString());
        }
        finally
        {
            Console.SetError(originalErr);
        }
    }
}
