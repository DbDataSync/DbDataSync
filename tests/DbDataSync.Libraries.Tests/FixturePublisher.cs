using System.Collections.Concurrent;
using System.Diagnostics;

namespace DbDataSync.Libraries.Tests;

/// <summary>
/// Publishes one of <c>tests/fixtures/DbDataSync.Libraries.FactoryFixture*</c> once per test run and
/// hands back its output directory — the same "throwaway project → <c>dotnet publish</c> → flat
/// output" shape <c>DbDataSync.Libraries.LibraryInstaller</c> uses for a restored library, and the same
/// pattern <c>DbDataSync.Drivers.Loader.Tests.FixturePublisher</c> already established for a compiled
/// driver plugin.
/// </summary>
public static class FixturePublisher
{
    private static readonly ConcurrentDictionary<string, Lazy<string>> Published = new();

    public static string OutputDirectoryFor(string fixtureProjectName) =>
        Published.GetOrAdd(fixtureProjectName, name => new Lazy<string>(() => PublishOnce(name))).Value;

    private static string PublishOnce(string fixtureProjectName)
    {
        var repoRoot = FindRepoRoot();
        var fixtureProject = Path.Combine(repoRoot, "tests", "fixtures", fixtureProjectName, $"{fixtureProjectName}.csproj");
        if (!File.Exists(fixtureProject))
            throw new FileNotFoundException($"Fixture project not found at '{fixtureProject}'.", fixtureProject);

        var outDir = Path.Combine(Path.GetTempPath(), $"dbdatasync-factory-fixture-{Guid.NewGuid():N}");
        var startInfo = new ProcessStartInfo
        {
            FileName = "dotnet",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var arg in new[] { "publish", fixtureProject, "-c", "Release", "-o", outDir, "--nologo" })
            startInfo.ArgumentList.Add(arg);

        using var process = Process.Start(startInfo)!;
        // Both streams read concurrently — see DbDataSync.Drivers.Loader.Tests.FixturePublisher for why
        // reading them one after the other can deadlock the child against the parent.
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        Task.WaitAll(stdoutTask, stderrTask);
        process.WaitForExit();
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"Publishing fixture '{fixtureProjectName}' failed:\n{stdoutTask.Result}\n{stderrTask.Result}");

        return outDir;
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "DbDataSync.slnx")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Could not find the repo root (DbDataSync.slnx).");
    }
}
