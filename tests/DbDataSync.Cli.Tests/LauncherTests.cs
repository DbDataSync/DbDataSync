using System.Diagnostics;
using System.Reflection;
using DbDataSync.Updates;

namespace DbDataSync.Cli.Tests;

/// <summary>
/// Phase 196L: the real launcher, run as a process, calling into a real build of this tool laid out as a slot. The
/// build of <c>DbDataSync.Cli</c> copies this machine's launcher to <c>launcher/&lt;rid&gt;/</c> beside itself, so
/// both halves are the ones that ship.
/// <para>
/// The slot's <c>tools/net10.0/any</c> is a symlink to that build output rather than a copy (it is hundreds of
/// megabytes) — hence non-Windows, where a symlink needs no privilege.
/// </para>
/// </summary>
public sealed class LauncherTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "dbdatasync-launcher-" + Guid.NewGuid().ToString("N"));

    public LauncherTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [NonWindowsFact]
    public void RunsTheSlotThePointerNames_AndExitsWithItsExitCode()
    {
        LayOut("a");
        File.WriteAllText(SlotPaths.PointerPath(_root), "a\n");

        var version = Run("version");
        Assert.Equal(0, version.ExitCode);
        Assert.Equal(
            typeof(Help).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion,
            version.Output.Trim());

        var unknown = Run("no-such-command");
        Assert.Equal(1, unknown.ExitCode);
        Assert.Contains("Unknown command 'no-such-command'", unknown.Error);
    }

    [NonWindowsFact]
    public void WithNoPointer_ItSaysWhatToWrite_AndRunsNothing()
    {
        LayOut("a");

        var result = Run("version");

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("current.txt is missing", result.Error);
        Assert.Equal("", result.Output);
    }

    [NonWindowsFact]
    public void APointerAtAnEmptySlot_NamesTheOtherSlotAsTheFix()
    {
        LayOut("a");
        File.WriteAllText(SlotPaths.PointerPath(_root), "b");

        var result = Run("version");

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("names slot b", result.Error);
        Assert.Contains("write \"a\"", result.Error);
    }

    /// <summary>The launcher into the root; this build of the tool into <paramref name="slot"/>, as version 9.9.9.</summary>
    private void LayOut(string slot)
    {
        var build = CliBuildDirectory();
        var rid = SlotPaths.PortableRuntimeIdentifier()!;
        var launcher = Path.Combine(build, SlotPaths.LauncherDirectoryName, rid);
        Assert.True(Directory.Exists(launcher), $"The build of DbDataSync.Cli has no {launcher} — its CopyOwnLauncher target did not run.");

        foreach (var file in Directory.EnumerateFiles(launcher))
            File.Copy(file, Path.Combine(_root, Path.GetFileName(file)));

        var tools = Path.Combine(SlotPaths.SlotDirectory(_root, slot), ".store", "dbdatasync", "9.9.9", "dbdatasync", "9.9.9", "tools", "net10.0");
        Directory.CreateDirectory(tools);
        Directory.CreateSymbolicLink(Path.Combine(tools, "any"), build);
    }

    private (int ExitCode, string Output, string Error) Run(params string[] args)
    {
        var start = new ProcessStartInfo(Path.Combine(_root, SlotPaths.LauncherName))
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var arg in args)
            start.ArgumentList.Add(arg);

        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        Assert.True(process.WaitForExit(TimeSpan.FromSeconds(60)), "The launcher did not exit within a minute.");
        return (process.ExitCode, output.Result, error.Result);
    }

    /// <summary><c>src/DbDataSync.Cli/bin/&lt;configuration&gt;/net10.0</c> — the tool's own output, with its own
    /// deps.json and runtimeconfig, which this test project's copy of the dll does not have.</summary>
    private static string CliBuildDirectory()
    {
        var here = new DirectoryInfo(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar));
        var framework = here.Name;
        var configuration = here.Parent!.Name;
        for (var dir = here; dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "DbDataSync.slnx")))
                return Path.Combine(dir.FullName, "src", "DbDataSync.Cli", "bin", configuration, framework);
        }

        throw new DirectoryNotFoundException("The repository root was not found above " + AppContext.BaseDirectory);
    }
}
