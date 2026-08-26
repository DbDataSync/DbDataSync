using System.Diagnostics;
using System.Runtime.InteropServices;

namespace DataSync.DevHarness;

/// <summary>
/// Runs the API and the Vite dev server as child processes for the lifetime of an <c>up</c>, tailing
/// their output and taking them down together.
/// </summary>
public sealed class AppProcesses : IAsyncDisposable
{
    private readonly List<Process> _processes = [];

    /// <summary>
    /// Where the API keeps its git-backed config and state database for a harness session.
    /// <para>
    /// Under the OS temp directory, deliberately *outside* this repository's working tree:
    /// LibGit2Sharp's repository discovery walks up parent directories, so a scratch repo nested
    /// inside the project tree can be mistaken for the project's own repository. Same reasoning, and
    /// the same location convention, as <c>tests/DataSync.Web.Tests/playwright.config.ts</c>.
    /// </para>
    /// </summary>
    public static string ScratchRepoRoot { get; } = Path.Combine(Path.GetTempPath(), "datasync-dev-harness-repo");

    public static string StateDbPath => Path.Combine(ScratchRepoRoot, "state.db");

    public static void ResetScratchRepo()
    {
        if (Directory.Exists(ScratchRepoRoot))
            Directory.Delete(ScratchRepoRoot, recursive: true);
        Directory.CreateDirectory(ScratchRepoRoot);
    }

    public void StartApi(string repoRoot, int port)
    {
        var dll = Path.Combine(repoRoot, "src", "DataSync.Api", "bin", "Debug", "net10.0", "DataSync.Api.dll");
        if (!File.Exists(dll))
            throw new HarnessException($"The API isn't built ({dll} is missing). Run `dotnet build` first.");

        Log.Step($"Starting the API on http://127.0.0.1:{port}");

        // `dotnet exec` on the built DLL rather than `dotnet run --project`: the latter spawns a
        // wrapper process around the real one, and killing the wrapper doesn't reliably kill its
        // child — which would leave an API instance running against a scratch repo this harness has
        // since deleted. Same lesson as playwright.config.ts's webServer command.
        var startInfo = new ProcessStartInfo
        {
            FileName = "dotnet",
            WorkingDirectory = repoRoot,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        startInfo.ArgumentList.Add("exec");
        startInfo.ArgumentList.Add(dll);

        startInfo.Environment["ASPNETCORE_URLS"] = $"http://127.0.0.1:{port}";
        startInfo.Environment["ASPNETCORE_ENVIRONMENT"] = "Development";
        startInfo.Environment["DataSync__RepoRoot"] = ScratchRepoRoot;
        startInfo.Environment["DataSync__StateDbPath"] = StateDbPath;
        // Logging__LogLevel__Default: the API's request logging is far too chatty to tail alongside a
        // workload; warnings and above still surface anything that actually matters.
        startInfo.Environment["Logging__LogLevel__Default"] = "Warning";

        // The spawned TaskRunner inherits these. In an environment with no OS keychain, SecretStore
        // falls back to CLRKERNEL_SECRET_* variables — without them a run fails to resolve the
        // connection password. Same workaround the Playwright suite and the integration tests use.
        foreach (var name in new[] { Scenario.SourceConnectionName, Scenario.TargetConnectionName })
            startInfo.Environment[SecretEnvVarName(name)] = Scenario.SaPassword;

        Start(startInfo, "api");
    }

    public void StartSpa(string repoRoot, int port, int apiPort)
    {
        var webRoot = Path.Combine(repoRoot, "src", "DataSync.Web");
        if (!Directory.Exists(Path.Combine(webRoot, "node_modules")))
        {
            Log.Step("Installing SPA dependencies (first run only)");
            RunToCompletion(webRoot, NpmCommand("install"));
        }

        Log.Step($"Starting the SPA on http://127.0.0.1:{port}");
        var startInfo = NpmCommand("run", "dev", "--", "--port", port.ToString(), "--strictPort");
        startInfo.WorkingDirectory = webRoot;
        startInfo.RedirectStandardOutput = true;
        startInfo.RedirectStandardError = true;
        startInfo.Environment["DATASYNC_API_URL"] = $"http://127.0.0.1:{apiPort}";

        Start(startInfo, "spa");
    }

    /// <summary>
    /// npm is a shell script on Unix but a <c>.cmd</c> batch file on Windows, which
    /// <c>UseShellExecute = false</c> cannot execute directly — it has to go through cmd.exe.
    /// </summary>
    private static ProcessStartInfo NpmCommand(params string[] args)
    {
        var startInfo = new ProcessStartInfo { UseShellExecute = false };

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            startInfo.FileName = "cmd.exe";
            startInfo.ArgumentList.Add("/c");
            startInfo.ArgumentList.Add("npm");
        }
        else
        {
            startInfo.FileName = "npm";
        }

        foreach (var arg in args)
            startInfo.ArgumentList.Add(arg);

        return startInfo;
    }

    private void Start(ProcessStartInfo startInfo, string tag)
    {
        Process process;
        try
        {
            process = Process.Start(startInfo) ?? throw new HarnessException($"Failed to start the {tag} process.");
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            throw new HarnessException($"Could not start the {tag} process ({startInfo.FileName}): {ex.Message}");
        }

        process.OutputDataReceived += (_, e) => { if (e.Data is not null) Log.Child(tag, e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) Log.Child(tag, e.Data); };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        _processes.Add(process);
    }

    private static void RunToCompletion(string workingDirectory, ProcessStartInfo startInfo)
    {
        startInfo.WorkingDirectory = workingDirectory;
        using var process = Process.Start(startInfo) ?? throw new HarnessException("Failed to start npm.");
        process.WaitForExit();
        if (process.ExitCode != 0)
            throw new HarnessException($"npm exited with code {process.ExitCode}.");
    }

    public static string SecretEnvVarName(string connectionName)
    {
        var key = $"datasync:connection:{connectionName}".ToUpperInvariant();
        var sanitized = new string(key.Select(c => char.IsLetterOrDigit(c) ? c : '_').ToArray());
        return $"CLRKERNEL_SECRET_{sanitized}";
    }

    /// <summary>Completes when any child exits, so <c>up</c> stops pretending the environment is
    /// healthy after the API has fallen over.</summary>
    public async Task<string?> WaitForAnyExitAsync(CancellationToken cancellationToken)
    {
        var exits = _processes.Select(async p =>
        {
            await p.WaitForExitAsync(cancellationToken);
            return p;
        }).ToList();

        var finished = await Task.WhenAny(exits);
        var process = await finished;
        return $"a child process exited with code {process.ExitCode}";
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var process in _processes)
        {
            try
            {
                if (!process.HasExited)
                    // entireProcessTree: Vite spawns its own children, and the API spawns TaskRunner
                    // workers — killing only the parent would strand them holding ports and the
                    // scratch state database.
                    process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None);
            }
            catch (InvalidOperationException)
            {
                // Already gone between the check and the kill.
            }
            finally
            {
                process.Dispose();
            }
        }

        _processes.Clear();
    }
}
