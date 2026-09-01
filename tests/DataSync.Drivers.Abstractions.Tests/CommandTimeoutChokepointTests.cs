using Xunit;

namespace DataSync.Drivers.Abstractions.Tests;

/// <summary>
/// The chokepoint is only worth having if everything goes through it, and nothing about the type
/// system enforces that: <c>CreateCommand()</c> stays available on every <c>DbConnection</c>, and a
/// call to it compiles, runs, and silently gets the provider's 30-second default. That is exactly the
/// bug phase 76 fixed, so this is the test that stops it growing back.
/// <para>
/// A source scan rather than an assertion about behaviour, because the claim *is* about the source:
/// "no source/target command is raised any other way". There was no precedent for reading <c>src/</c>
/// from a test in this repo before this one — the alternative was a per-class test for each of the
/// sixty-odd call sites, which would assert less and cost more, and would still say nothing about the
/// sixty-first.
/// </para>
/// </summary>
public sealed class CommandTimeoutChokepointTests
{
    /// <summary>
    /// Projects whose commands run against a source or target, and must therefore carry the operator's
    /// configured timeout.
    /// <para>
    /// <c>DataSync.State</c> is deliberately absent — an internal, single-writer store this process
    /// owns, a different risk profile from a network hop to a database somebody else operates, and
    /// out of scope per the planning doc. If it ever adopts this, it does so on its own terms.
    /// </para>
    /// </summary>
    private static readonly string[] InScope =
    [
        "DataSync.Core/Sql",
        "DataSync.Drivers.Generic",
        "DataSync.Drivers.MsSql",
        "DataSync.Drivers.Postgres",
        "DataSync.Scripting",
        "DataSync.Api",
        "DataSync.TaskRunner",
        "DataSync.Verification",
    ];

    /// <summary>
    /// The two files allowed to say <c>CreateCommand()</c>.
    /// <list type="bullet">
    /// <item><c>ConnectionTimeouts.cs</c> is the chokepoint — it has to call the thing it wraps.</item>
    /// <item><c>VerificationResultQuery.cs</c> queries DuckDB over a local parquet file. No
    /// <c>ConnectionConfig</c>, no source or target, no network hop, and nothing to resolve a timeout
    /// from.</item>
    /// </list>
    /// </summary>
    private static readonly string[] Exempt = ["ConnectionTimeouts.cs", "VerificationResultQuery.cs"];

    [Fact]
    public void EverySourceOrTargetCommandGoesThroughCreateTimedCommand()
    {
        var src = Path.Combine(RepositoryRoot(), "src");

        var offenders = InScope
            .SelectMany(area => Directory.EnumerateFiles(Path.Combine(src, area), "*.cs", SearchOption.AllDirectories))
            // bin/obj hold generated and copied sources that are not what anybody edits.
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                && !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .Where(path => !Exempt.Contains(Path.GetFileName(path)))
            .Where(path => File.ReadAllText(path).Contains(".CreateCommand()", StringComparison.Ordinal))
            .Select(path => Path.GetRelativePath(src, path))
            .Order()
            .ToList();

        Assert.True(
            offenders.Count == 0,
            "These raise commands that will not carry the connection's configured CommandTimeout. Use "
            + $"CreateTimedCommand() instead:{Environment.NewLine}  {string.Join($"{Environment.NewLine}  ", offenders)}");
    }

    /// <summary>Walks up from the test binary to the directory holding the solution file.</summary>
    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "DataSync.slnx")))
            directory = directory.Parent;

        Assert.NotNull(directory);
        return directory!.FullName;
    }
}
