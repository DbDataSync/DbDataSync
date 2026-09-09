using System.Diagnostics;
using System.Runtime.InteropServices;

namespace DbDataSync.Drivers.Loader.Tests;

/// <summary>
/// Publishes <c>tests/fixtures/DbDataSync.Drivers.LoaderTestFixture</c> once per test run and hands
/// back its output directory — the same "throwaway project → <c>dotnet publish</c> → flat output"
/// shape <c>DbDataSync.Providers.ProviderInstaller</c> uses for a restored provider, except this
/// project already exists in the repo rather than being written on the fly.
/// </summary>
public static class FixturePublisher
{
    private static readonly Lazy<string> PublishedDirectory = new(PublishOnce);

    public static string OutputDirectory => PublishedDirectory.Value;

    private static string PublishOnce()
    {
        var repoRoot = FindRepoRoot();
        var fixtureProject = Path.Combine(repoRoot, "tests", "fixtures", "DbDataSync.Drivers.LoaderTestFixture", "DbDataSync.Drivers.LoaderTestFixture.csproj");
        if (!File.Exists(fixtureProject))
            throw new FileNotFoundException($"Fixture project not found at '{fixtureProject}'.", fixtureProject);

        var outDir = Path.Combine(Path.GetTempPath(), $"dbdatasync-loader-fixture-{Guid.NewGuid():N}");
        var startInfo = new ProcessStartInfo
        {
            FileName = "dotnet",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        // An explicit RuntimeIdentifier (framework-dependent, not self-contained) is what makes
        // `dotnet publish` flatten Microsoft.Data.Sqlite's runtimes/<rid>/native/ asset into the
        // output at all — a portable, RID-less publish skips native assets entirely. Same lesson
        // ProviderInstaller already learned for a restored provider's own native assets.
        var rid = RuntimeInformation.RuntimeIdentifier;
        foreach (var arg in new[]
        {
            "publish", fixtureProject, "-c", "Release", "-o", outDir, "--nologo",
            "-r", rid, "--self-contained", "false",
        })
            startInfo.ArgumentList.Add(arg);

        using var process = Process.Start(startInfo)!;
        // Both streams read concurrently, not one after the other: dotnet publish can write enough to
        // stderr to fill the OS pipe buffer while this reads stdout to completion first, which is a
        // classic cross-process deadlock (the child blocks writing, the parent blocks reading the
        // other stream) — caught by this hanging in practice before the fix.
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        Task.WaitAll(stdoutTask, stderrTask);
        process.WaitForExit();
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"Publishing the loader test fixture failed:\n{stdoutTask.Result}\n{stderrTask.Result}");

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
