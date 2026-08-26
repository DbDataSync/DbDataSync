using DataSync.TaskRunner;
using Xunit;

namespace DataSync.TaskRunner.Tests;

public sealed class TaskRunnerOptionsTests
{
    [Fact]
    public void TryParse_WithAllRequiredArgs_Succeeds()
    {
        var args = new[] { "--repo-root", "/repo", "--state-db", "/repo/state.db", "--replication", "crm-sync" };

        var ok = TaskRunnerOptions.TryParse(args, out var options, out var error);

        Assert.True(ok);
        Assert.Null(error);
        Assert.Equal("/repo", options!.RepoRoot);
        Assert.Equal("/repo/state.db", options.StateDbPath);
        Assert.Equal("crm-sync", options.Replication);
        Assert.Equal(Path.Combine("/repo", "config"), options.ConfigRoot);
        Assert.Equal(4, options.DegreeOfParallelism);
    }

    [Fact]
    public void TryParse_WithExplicitDegreeOfParallelism_UsesIt()
    {
        var args = new[]
        {
            "--repo-root", "/repo", "--state-db", "/repo/state.db", "--replication", "crm-sync",
            "--degree-of-parallelism", "8",
        };

        var ok = TaskRunnerOptions.TryParse(args, out var options, out _);

        Assert.True(ok);
        Assert.Equal(8, options!.DegreeOfParallelism);
    }

    [Fact]
    public void TryParse_MissingRequiredArgs_Fails()
    {
        var ok = TaskRunnerOptions.TryParse(["--repo-root", "/repo"], out var options, out var error);

        Assert.False(ok);
        Assert.Null(options);
        Assert.Contains("--state-db", error);
        Assert.Contains("--replication", error);
    }

    [Fact]
    public void TryParse_InvalidDegreeOfParallelism_Fails()
    {
        var args = new[]
        {
            "--repo-root", "/repo", "--state-db", "/repo/state.db", "--replication", "crm-sync",
            "--degree-of-parallelism", "not-a-number",
        };

        var ok = TaskRunnerOptions.TryParse(args, out var options, out var error);

        Assert.False(ok);
        Assert.Null(options);
        Assert.Contains("not-a-number", error);
    }

    [Fact]
    public void TryParse_ZeroDegreeOfParallelism_Fails()
    {
        var args = new[]
        {
            "--repo-root", "/repo", "--state-db", "/repo/state.db", "--replication", "crm-sync",
            "--degree-of-parallelism", "0",
        };

        var ok = TaskRunnerOptions.TryParse(args, out var options, out var error);

        Assert.False(ok);
        Assert.Null(options);
    }

    [Fact]
    public void TryParse_UnrecognizedArg_Fails()
    {
        var ok = TaskRunnerOptions.TryParse(["--bogus", "x"], out var options, out var error);

        Assert.False(ok);
        Assert.Null(options);
        Assert.Contains("--bogus", error);
    }
}
