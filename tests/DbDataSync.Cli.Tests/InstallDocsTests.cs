namespace DbDataSync.Cli.Tests;

/// <summary>
/// <c>docs/install.md</c> mirrors <see cref="CliOptions.DefaultToolDir"/>'s literal values by hand (a
/// plain Markdown file can't reference the constant) — this is what keeps the two from drifting apart
/// silently: if the value ever changes, this fails instead of the doc quietly going stale.
/// </summary>
public sealed class InstallDocsTests
{
    [Fact]
    public void InstallDocs_MentionThisPlatformsDefaultToolDir()
    {
        var docsPath = Path.Combine(FindRepoRoot(), "docs", "install.md");

        Assert.True(File.Exists(docsPath), $"'{docsPath}' does not exist.");
        var text = File.ReadAllText(docsPath);

        // docs/install.md spells the Windows directory as PowerShell's `$env:ProgramFiles\DbDataSync`,
        // not the expanded `C:\Program Files\DbDataSync` — deliberately, since %ProgramFiles% is both
        // relocatable and localized and the literal would be wrong advice on plenty of real machines.
        // Rebuilding that same spelling from the constant keeps the drift-detection this test exists
        // for: rename or re-root DefaultToolDir and the string stops appearing in the doc. Phase 140,
        // where a real windows-latest run first evaluated this on the platform it is named after.
        var documented = OperatingSystem.IsWindows()
            ? @"$env:ProgramFiles\" + Path.GetRelativePath(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), CliOptions.DefaultToolDir)
            : CliOptions.DefaultToolDir;

        Assert.Contains(documented, text);
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "DbDataSync.slnx")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Could not find the repo root (DbDataSync.slnx).");
    }
}
