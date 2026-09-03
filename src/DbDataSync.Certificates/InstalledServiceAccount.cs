using System.Diagnostics;
using System.Runtime.Versioning;

namespace DbDataSync.Certificates;

/// <summary>
/// Reads back which account the installed DbDataSync Windows service runs as — the second rung of the
/// phase 82 doc's "which account" resolution order for the issuing commands: an explicit
/// <c>--account</c> wins outright, otherwise this, otherwise <c>LocalSystem</c> (which needs no grant).
/// <para>
/// **The doc's own text says this reads the account "the same way <c>dbdatasync service status</c>
/// already parses it."** That turned out not to be true: <c>ServiceCommand.Run</c>'s <c>status</c> case
/// is a bare passthrough of <c>sc.exe query</c>'s output to the console (<c>Sc("query", ServiceName)</c>
/// in <c>src/DbDataSync.Cli/ServiceCommand.cs</c>) — it never parses anything. There was no existing
/// parser to reuse, so this is a fresh one, over <c>sc.exe qc</c> (query config, which is what actually
/// reports <c>SERVICE_START_NAME</c> — <c>sc.exe query</c> reports run-time status, not configuration).
/// </para>
/// </summary>
public static class InstalledServiceAccount
{
    /// <summary>The configured account, or null if the service isn't installed, <c>sc.exe</c> could not
    /// be run, or the output didn't parse — every failure here is "fall through to LocalSystem," never
    /// an exception, since not having an installed service to read from is an entirely ordinary state
    /// (a fresh machine, a deployment that never uses the Windows service at all).
    /// <para>Marked here rather than on the whole class — <see cref="ParseServiceStartName"/>, below,
    /// is plain string parsing with nothing OS-specific about it, and a class-level attribute would
    /// have dragged it under the same gate as the actual <c>sc.exe</c> call for no reason.</para>
    /// </summary>
    [SupportedOSPlatform("windows")]
    public static string? Resolve(string serviceName)
    {
        var startInfo = new ProcessStartInfo("sc.exe")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        startInfo.ArgumentList.Add("qc");
        startInfo.ArgumentList.Add(serviceName);

        try
        {
            using var process = Process.Start(startInfo);
            if (process is null)
                return null;

            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit();
            return process.ExitCode == 0 ? ParseServiceStartName(output) : null;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>
    /// The pure half — pulled out so the parsing itself is a unit test against a literal, hand-captured
    /// sample of real <c>sc.exe qc</c> output, runnable on any OS with no <c>sc.exe</c> and no installed
    /// service required. Not marked <see cref="SupportedOSPlatformAttribute"/>: parsing a string has
    /// nothing OS-specific about it, even though the only realistic way to produce that string does.
    /// </summary>
    internal static string? ParseServiceStartName(string scQcOutput)
    {
        foreach (var rawLine in scQcOutput.Split('\n'))
        {
            var line = rawLine.Trim();
            if (!line.StartsWith("SERVICE_START_NAME", StringComparison.OrdinalIgnoreCase))
                continue;

            var separator = line.IndexOf(':');
            if (separator < 0)
                return null;

            var value = line[(separator + 1)..].Trim();
            return string.IsNullOrEmpty(value) ? null : value;
        }

        return null;
    }
}
