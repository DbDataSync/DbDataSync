using DbDataSync.Libraries;

namespace DbDataSync.Cli.Tests;

/// <summary>
/// Phase 109i: <c>ServeCommand.EnsureDuckDbInstalledAsync</c> — the unconditional, idempotent DuckDB
/// install every <c>serve</c> start (and, through it, <c>setup</c>'s own Start button) makes. Real
/// installs, not fakes — the same "real, not mocked" precedent 109h's own
/// <c>SetupStepsTests</c>/<c>DbDataSync.State.Tests</c>' <c>LibraryInstallFixture</c> already
/// established, because a fake that only writes a manifest without a real <c>lib/</c> directory would
/// prove nothing about whether the assembly is actually loadable afterward.
/// </summary>
public sealed class ServeCommandDuckDbTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("dbdatasync-serve-duckdb-tests-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { /* best effort */ }
    }

    [Fact]
    public async Task FreshRoot_InstallsDuckDbForReal()
    {
        await ServeCommand.EnsureDuckDbInstalledAsync(_root);

        var registry = new LibraryRegistry(_root).LoadAll();
        Assert.True(registry.Installed.TryGetValue("duckdb", out var manifest));
        Assert.Equal("DuckDB.NET.Data.DuckDBClientFactory, DuckDB.NET.Data", manifest!.FactoryType);
        Assert.True(File.Exists(Path.Combine(_root, "libraries", "duckdb", "lib", "DuckDB.NET.Data.dll")));
    }

    /// <summary>
    /// The idempotency check is a plain directory check (see the method's own doc comment), not a
    /// registry load — asserted here by dropping a marker file into an already-"installed" <c>lib/</c>
    /// and confirming it survives. <see cref="LibraryInstaller.RestorePackagesAsync"/> always deletes
    /// and fully recreates the target <c>lib/</c> directory when it actually runs, so a marker file
    /// surviving the call is proof no restore happened — the acceptance bar the phase doc set for "a
    /// second `serve` start does no restore work".
    /// </summary>
    [Fact]
    public async Task AlreadyInstalled_DoesNotReinstall()
    {
        var libDir = Path.Combine(_root, "libraries", "duckdb", "lib");
        Directory.CreateDirectory(libDir);
        var marker = Path.Combine(libDir, "marker.txt");
        await File.WriteAllTextAsync(marker, "already here");

        await ServeCommand.EnsureDuckDbInstalledAsync(_root);

        Assert.True(File.Exists(marker));
    }

    /// <summary>
    /// A failed install (here: no `library.json` written because the restore itself never ran — forced
    /// by pointing at a directory that can never resolve any package, see <see cref="BrokenRoot"/>) is
    /// logged and swallowed, not rethrown — the same "shouldn't be allowed to take the whole process
    /// down" posture <c>CertificateExpiryService</c> already takes. This does not assert on the console
    /// output (this repo has no seam to intercept <c>ServeCommand</c>'s <c>Console.Error</c> without
    /// reworking its plain-static shape, which is out of scope here); it asserts the one thing that
    /// actually matters — the method returns normally rather than throwing.
    /// </summary>
    [Fact]
    public async Task FailedInstall_DoesNotThrow()
    {
        // A root under a path component that cannot exist as a directory (a file sits where a
        // directory is expected) makes LibraryInstaller's own Directory.CreateDirectory calls throw —
        // a real, reproducible failure with no network dependency, unlike a bad package version (which
        // needs internet access to observe deterministically).
        var blockingFile = Path.Combine(_root, "blocked");
        File.WriteAllText(blockingFile, "");
        var unreachableRoot = Path.Combine(blockingFile, "repo");

        await ServeCommand.EnsureDuckDbInstalledAsync(unreachableRoot);
    }
}
