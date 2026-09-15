using System.Diagnostics;
using ClrKernel.Core.Secrets;
using DbDataSync.Core.Config;
using DbDataSync.Core.Git;
using DbDataSync.State.Remote;
using LibGit2Sharp;

namespace DbDataSync.TaskRunner.Tests;

/// <summary>
/// The runner as a real process, launched the way the API launches it, against an owner that is not
/// there. Everything else about phase 39 can be tested in-process; that this wiring reaches the right
/// exit code cannot.
/// </summary>
public sealed class RunnerWithoutItsOwnerTests : IDisposable
{
    private readonly string _repoRoot = Directory.CreateTempSubdirectory("dbdatasync-runner-exit-").FullName;

    public void Dispose() => GitTempDirectory.DeleteRecursively(_repoRoot);

    /// <summary>A replication the runner can actually load, so that what these tests observe is the
    /// state wiring rather than a config error on the way to it.</summary>
    private void SaveReplication()
    {
        Repository.Init(_repoRoot);
        new ConfigRepository(
                Path.Combine(_repoRoot, "config"), new GitCommitService(_repoRoot),
                SecretStore.ForProviders([new InMemorySecretProvider()]))
            .SaveReplicationTask(new ReplicationTaskConfig
            {
                Name = "sales",
                Scheduling = new SchedulingConfig
            {
                Mode = ScheduleMode.Continuous,
                // A continuous worker now stays resident until it has gone a whole idle timeout
                // without a pass reading anything. These tests drain a queue and want the worker to
                // leave promptly, so they say so — the production default is 60 seconds.
                FrequencySeconds = 1,
                IdleTimeoutSeconds = 2,
            },
                ChangeProcessing = new ChangeProcessingConfig
                {
                    Reader = new ReaderConfig { Kind = "MsSqlChangeTracking" },
                    Cache = new CacheConfig { Kind = "MsSqlStagingTable" },
                    Writer = new WriterConfig { Kind = "MsSqlMerge" },
                },
            }, new GitAuthor("Test", "test@example.com"));
    }

    private async Task<(int ExitCode, string Stderr)> RunAsync(Action<ProcessStartInfo> configure)
    {
        SaveReplication();

        var startInfo = new ProcessStartInfo
        {
            FileName = "dotnet",
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
        };
        startInfo.ArgumentList.Add("exec");
        startInfo.ArgumentList.Add(TaskRunnerDllPath);
        startInfo.ArgumentList.Add("--repo-root");
        startInfo.ArgumentList.Add(_repoRoot);
        startInfo.ArgumentList.Add("--state-db");
        startInfo.ArgumentList.Add(Path.Combine(_repoRoot, "state.db"));
        startInfo.ArgumentList.Add("--replication");
        startInfo.ArgumentList.Add("sales");
        configure(startInfo);

        using var process = Process.Start(startInfo)!;
        var stderr = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return (process.ExitCode, stderr);
    }

    /// <summary>
    /// A runner started by hand while the API is up would be a second writer to the state file, which
    /// is the thing this whole phase exists to prevent. Refusing at startup is the correct outcome —
    /// and it must not be a silent success.
    /// </summary>
    [Fact]
    public async Task Without_a_state_endpoint_it_refuses_to_start_rather_than_opening_the_file()
    {
        var (exitCode, stderr) = await RunAsync(_ => { });

        Assert.NotEqual(0, exitCode);
        Assert.Contains("never opens the state file directly", stderr);
        Assert.False(File.Exists(Path.Combine(_repoRoot, "state.db")));
    }

    /// <summary>
    /// The exit code the API reads to tell "the work failed" from "the work may well have succeeded
    /// and the record of it is on disk". Nothing is listening on port 1.
    /// </summary>
    [Fact]
    public async Task With_an_owner_that_never_answers_it_exits_with_the_distinct_code()
    {
        var (exitCode, stderr) = await RunAsync(startInfo =>
        {
            startInfo.ArgumentList.Add("--state-grace-seconds");
            startInfo.ArgumentList.Add("1");
            startInfo.Environment[StateProtocol.EndpointEnvironmentVariable] = "http://127.0.0.1:1";
            startInfo.Environment[StateProtocol.TokenEnvironmentVariable] = "a-token";
        });

        Assert.Equal((int)ExitCode.StateOwnerUnavailable, exitCode);
        Assert.Contains("State owner unavailable", stderr);
    }

    [Fact]
    public async Task An_endpoint_without_a_token_is_refused__the_token_is_never_an_argument()
    {
        var (exitCode, stderr) = await RunAsync(startInfo =>
        {
            startInfo.ArgumentList.Add("--state-endpoint");
            startInfo.ArgumentList.Add("http://127.0.0.1:1");
            startInfo.Environment.Remove(StateProtocol.TokenEnvironmentVariable);
        });

        Assert.NotEqual(0, exitCode);
        Assert.Contains(StateProtocol.TokenEnvironmentVariable, stderr);
    }

    private static string TaskRunnerDllPath
    {
        get
        {
            var baseDir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
            var tfm = Path.GetFileName(baseDir);
            var configuration = Path.GetFileName(Path.GetDirectoryName(baseDir))!;

            var repoRoot = new DirectoryInfo(baseDir);
            while (repoRoot is not null && !File.Exists(Path.Combine(repoRoot.FullName, "DbDataSync.slnx")))
                repoRoot = repoRoot.Parent;

            return Path.Combine(
                repoRoot!.FullName, "src", "DbDataSync.TaskRunner", "bin", configuration, tfm, "DbDataSync.TaskRunner.dll");
        }
    }
}
