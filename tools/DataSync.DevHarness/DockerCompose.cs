using System.Diagnostics;
using Microsoft.Data.SqlClient;

namespace DataSync.DevHarness;

/// <summary>Drives docker-compose.yml, and waits for the servers the way a client actually needs
/// them.</summary>
public static class DockerCompose
{
    public static void Up(string repoRoot)
    {
        Log.Step("Starting SQL Server containers");
        Run(repoRoot, "compose", "up", "-d");
    }

    public static void Down(string repoRoot, bool removeVolumes)
    {
        Log.Step(removeVolumes ? "Stopping containers and discarding their data" : "Stopping containers");
        string[] args = removeVolumes ? ["compose", "down", "-v"] : ["compose", "down"];
        Run(repoRoot, args);
    }

    /// <summary>
    /// Waits until both instances accept a connection *from here*. Deliberately not the compose
    /// healthcheck: that runs sqlcmd inside the container, so it proves the server can reach itself
    /// and says nothing about the published port being ready for a client on the host — which is the
    /// thing every subsequent step depends on.
    /// </summary>
    public static async Task WaitForServersAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        Log.Step("Waiting for both SQL Server instances to accept connections");
        await Task.WhenAll(
            WaitForServerAsync("source", Scenario.SourceConnectionString(), timeout, cancellationToken),
            WaitForServerAsync("target", Scenario.TargetConnectionString(), timeout, cancellationToken));
    }

    private static async Task WaitForServerAsync(
        string label, string connectionString, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        var attempt = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await using var connection = new SqlConnection(connectionString);
                await connection.OpenAsync(cancellationToken);
                await using var cmd = connection.CreateCommand();
                cmd.CommandText = "SELECT 1;";
                await cmd.ExecuteScalarAsync(cancellationToken);
                Log.Ok($"{label} is accepting connections");
                return;
            }
            catch (SqlException) when (DateTimeOffset.UtcNow < deadline)
            {
                // A first start pulls the image and initialises the instance; that legitimately takes
                // a while, so say so rather than looking hung.
                if (++attempt % 6 == 0)
                    Log.Info($"still waiting for {label} ({(int)(deadline - DateTimeOffset.UtcNow).TotalSeconds}s left)…");
                await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
            }
            catch (SqlException ex)
            {
                throw new HarnessException(
                    $"The {label} SQL Server did not accept a connection within {timeout.TotalSeconds:0}s: {ex.Message}\n" +
                    "Check `docker compose ps` and `docker compose logs`.");
            }
        }
    }

    private static void Run(string workingDirectory, params string[] args)
    {
        var startInfo = new ProcessStartInfo { FileName = "docker", WorkingDirectory = workingDirectory, UseShellExecute = false };
        foreach (var arg in args)
            startInfo.ArgumentList.Add(arg);

        Process process;
        try
        {
            process = Process.Start(startInfo)
                ?? throw new HarnessException("Failed to start docker.");
        }
        catch (System.ComponentModel.Win32Exception)
        {
            throw new HarnessException("Could not run `docker`. Is Docker installed and on PATH?");
        }

        process.WaitForExit();
        if (process.ExitCode != 0)
            throw new HarnessException($"`docker {string.Join(' ', args)}` exited with code {process.ExitCode}.");
    }
}
