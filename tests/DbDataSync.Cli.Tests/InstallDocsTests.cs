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

        Assert.Contains(CliOptions.DefaultToolDir, text);
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "DbDataSync.slnx")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Could not find the repo root (DbDataSync.slnx).");
    }
}
