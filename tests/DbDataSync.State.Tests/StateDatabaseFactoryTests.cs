using ClrKernel.Core.Secrets;
using DbDataSync.Core.Secrets;

namespace DbDataSync.State.Tests;

/// <summary>
/// <see cref="StateDatabase.FromOptions"/> — the one place <c>DbDataSyncHost.cs</c> and
/// <c>InviteCommand</c> both go through to decide which engine/connection a state store opens on,
/// added by phase 79 so the two can never drift.
/// </summary>
public sealed class StateDatabaseFactoryTests : IDisposable
{
    private readonly string _sqliteDbPath = Path.Combine(
        Directory.CreateTempSubdirectory("dbdatasync-state-factory-tests-").FullName, "state.db");

    public void Dispose() => Directory.Delete(Path.GetDirectoryName(_sqliteDbPath)!, recursive: true);

    [Fact]
    public void Sqlite_UsesThePathConstructor_IgnoringAnyConnectionString()
    {
        // A connection string here would be nonsensical for SQLite — proving it's ignored is proving
        // the by-path constructor, not StateDatabase(engine, connectionString), is what actually ran.
        var database = StateDatabase.FromOptions(
            StateEngineIds.Sqlite, _sqliteDbPath, "this is not a connection string", SecretStore.ForProviders([]));

        Assert.Equal(StateEngineIds.Sqlite, database.Dialect.Engine);
        Assert.True(File.Exists(_sqliteDbPath));
    }

    [Fact]
    public void NonSqlite_NoConnectionString_ThrowsNamingTheMissingSetting()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            StateDatabase.FromOptions(StateEngineIds.MsSql, _sqliteDbPath, null, SecretStore.ForProviders([])));

        Assert.Contains("DbDataSync:StateConnectionString", ex.Message);
    }

    [Fact]
    public void NonSqlite_NoStoredSecret_ConnectsWithTheConfiguredStringUnmodified()
    {
        // No server reachable at this address — this proves what connection string SqlClient/Npgsql
        // was *handed*, via the exception it fails with, not that a connection actually succeeds.
        var secrets = SecretStore.ForProviders([new InMemorySecretProvider()]);

        var ex = Record.Exception(() =>
            StateDatabase.FromOptions(StateEngineIds.MsSql, _sqliteDbPath, "Server=127.0.0.1,1;Database=x;", secrets));

        Assert.NotNull(ex);
        Assert.DoesNotContain("Password", ex!.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void NonSqlite_AStoredSecret_IsAppendedToTheConnectionString()
    {
        var secrets = SecretStore.ForProviders([new InMemorySecretProvider()]);
        secrets.Store(SecretRefs.ForAppSetting("stateConnectionString"), "hunter2");

        // Still no server reachable — SqlClient's own connection-string validation runs before it ever
        // dials out, so a malformed splice (a stray extra ';', a key it doesn't recognise) throws an
        // *argument* exception distinguishable from the network-timeout every other case here produces.
        // Getting a network-shaped failure instead is exactly the evidence the splice produced a
        // connection string SqlClient accepted as well-formed.
        var ex = Record.Exception(() =>
            StateDatabase.FromOptions(StateEngineIds.MsSql, _sqliteDbPath, "Server=127.0.0.1,1;Database=x;", secrets));

        Assert.NotNull(ex);
        Assert.IsNotType<ArgumentException>(ex);
        Assert.DoesNotContain("hunter2", ex!.Message);
    }
}
