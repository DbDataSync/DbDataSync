using ClrKernel.Core.Secrets;
using DbDataSync.Core.Secrets;
using DbDataSync.Libraries;

namespace DbDataSync.State.Tests;

/// <summary>
/// Phase 109g: <c>StateEngine: MsSql</c> (or <c>Postgres</c>) with the corresponding library not
/// installed fails at startup — inside <see cref="StateDatabase.FromOptions"/>, the same place every
/// real caller (<c>DbDataSyncHost.cs</c>, <c>InviteCommand</c>, the setup wizard) reaches — naming the
/// exact fix. Deliberately not a new message invented for this phase:
/// <see cref="MsSqlStateDialect.CreateConnection"/>/<see cref="PostgresStateDialect.CreateConnection"/>
/// call <see cref="LibraryRegistry.GetFactory"/> and let its own exception propagate unmodified, so this
/// is really a test that the failure reaches the caller at all — not a test of wording chosen twice.
/// </summary>
public sealed class StateLibraryMissingTests : IDisposable
{
    private readonly string _repoRoot = Directory.CreateTempSubdirectory("dbdatasync-state-nolib-").FullName;

    public void Dispose() => Directory.Delete(_repoRoot, recursive: true);

    [Theory]
    [InlineData(StateEngineIds.MsSql, "microsoft-data-sqlclient")]
    [InlineData(StateEngineIds.Postgres, "npgsql")]
    public void MissingLibrary_FailsAtStartup_NamingTheInstallCommand(string engine, string libraryId)
    {
        // No libraries/ directory at all under this repo root — nothing installed, LoadAll's ordinary
        // "silent no-op" case.
        var libraryRegistry = new LibraryRegistry(_repoRoot).LoadAll();
        var stateDbPath = Path.Combine(_repoRoot, "state.db");

        var ex = Assert.Throws<InvalidOperationException>(() => StateDatabase.FromOptions(
            engine, stateDbPath, "Server=127.0.0.1,1;Database=x;", SecretStore.ForProviders([]),
            libraryRegistry));

        Assert.Contains(libraryId, ex.Message);
        Assert.Contains($"dbdatasync config library install {libraryId}", ex.Message);
    }

    /// <summary>The failure happens before any network attempt — GetFactory refuses by
    /// <see cref="LibraryRegistry.Installed"/> membership alone, so this is fast and does not depend on
    /// a server (real or unreachable) being anywhere on the other end of the connection string.</summary>
    [Fact]
    public void MissingLibrary_FailsBeforeAnyConnectionAttempt()
    {
        var libraryRegistry = new LibraryRegistry(_repoRoot).LoadAll();

        // A connection string with no possible listener and a long timeout — if this took anywhere
        // near that timeout to fail, GetFactory's check would not have run first.
        var ex = Record.Exception(() => StateDatabase.FromOptions(
            StateEngineIds.MsSql, Path.Combine(_repoRoot, "state.db"),
            "Server=127.0.0.1,1;Database=x;Connect Timeout=30;", SecretStore.ForProviders([]), libraryRegistry));

        Assert.IsType<InvalidOperationException>(ex);
        Assert.Contains("is not installed", ex!.Message);
    }
}
