using System.Diagnostics;
using System.Text;
using DbDataSync.Api.Configuration;

namespace DbDataSync.Api.Services;

/// <summary>The child process's own report — <see cref="Output"/> is whatever it printed (its own
/// success message, or its own specific failure message), not a wrapped/reinterpreted version of it.
/// The API host never inspects the child's output beyond "did it exit 0" — the CLI command itself
/// already produces the operator-facing message.</summary>
public sealed record LibraryValidationLaunchResult(bool Succeeded, string Output);

/// <summary>
/// Phase 109j item 4: spawns <c>dbdatasync config library validate &lt;id&gt; --connection &lt;name&gt;</c>
/// as a genuine child process and waits for it — the same <c>dotnet exec &lt;dll&gt;</c> shape
/// <see cref="ProcessSupervisor.BuildStartInfo"/> already uses to spawn <c>DbDataSync.TaskRunner</c>,
/// reused here rather than reinvented.
/// <para>
/// **Why a child process, restated for this call site specifically**: the deep check actually opens a
/// real connection and exercises the driver's real staging provider/writer against whatever version of
/// its required library (e.g. <c>Microsoft.Data.SqlClient</c>) is currently installed — a real load into
/// that process's own <see cref="System.Runtime.Loader.AssemblyLoadContext.Default"/>, which never
/// unloads. Running this in the long-lived API host itself would mean every <c>validate</c> call
/// permanently grows that host's resident set with a library version it may never otherwise load (or
/// may later replace) — the exact hazard the phase doc names. This process starts, does the real work,
/// and exits; the API host that spawned it never touches the library at all.
/// </para>
/// <para>
/// Synchronous from the caller's own point of view — <see cref="RunAsync"/> awaits the child's exit
/// rather than firing-and-forgetting the way <see cref="ProcessSupervisor.EnsureWorkerRunning"/> does for
/// a long-running replication worker. A validate run is a single, bounded, operator-triggered check
/// (create a table, write a few rows, drop it), not an unbounded queue-drain, so there is no "started,
/// check back later" state worth introducing here.
/// </para>
/// </summary>
public sealed class LibraryValidationLauncher(ApiOptions options)
{
    public async Task<LibraryValidationLaunchResult> RunAsync(
        string libraryId, string connectionName, CancellationToken cancellationToken)
    {
        var startInfo = BuildStartInfo(options, libraryId, connectionName);

        using var process = new Process { StartInfo = startInfo };
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) stdout.AppendLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) stderr.AppendLine(e.Data); };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        await process.WaitForExitAsync(cancellationToken);

        var succeeded = process.ExitCode == 0;
        var output = succeeded
            ? stdout.ToString().Trim()
            : (stderr.ToString().Trim() is { Length: > 0 } err ? err : stdout.ToString().Trim());

        return new LibraryValidationLaunchResult(succeeded, output);
    }

    /// <summary>
    /// A value, not a side effect, for the same reason <see cref="ProcessSupervisor.BuildStartInfo"/> is
    /// — <paramref name="libraryId"/>/<paramref name="connectionName"/> are config-level names, never
    /// secrets (the connection's own credential is resolved *inside* the child, the identical way every
    /// other CLI command already does — see <c>LibraryCommand.ValidateAsync</c>), so both are plain
    /// command-line arguments rather than environment variables.
    /// </summary>
    internal static ProcessStartInfo BuildStartInfo(ApiOptions options, string libraryId, string connectionName)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "dotnet",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        startInfo.ArgumentList.Add("exec");
        startInfo.ArgumentList.Add(options.CliDllPath);
        startInfo.ArgumentList.Add("config");
        startInfo.ArgumentList.Add("library");
        startInfo.ArgumentList.Add("validate");
        startInfo.ArgumentList.Add(libraryId);
        startInfo.ArgumentList.Add("--connection");
        startInfo.ArgumentList.Add(connectionName);
        startInfo.ArgumentList.Add("--repo");
        startInfo.ArgumentList.Add(options.RepoRoot);
        return startInfo;
    }
}
