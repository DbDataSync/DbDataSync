namespace DataSync.State.Tests;

public sealed class StateDatabaseTests : IDisposable
{
    private readonly string _tempDir = Directory.CreateTempSubdirectory("datasync-state-tests-").FullName;

    public void Dispose() => Directory.Delete(_tempDir, recursive: true);

    [Fact]
    public void Constructing_CreatesSchema()
    {
        var dbPath = Path.Combine(_tempDir, "state.db");
        var database = new StateDatabase(dbPath);

        using var connection = database.OpenConnection();
        using var cmd = database.Command(connection, "SELECT name FROM sqlite_master WHERE type = 'table' AND name != 'sqlite_sequence' ORDER BY name;");
        using var reader = cmd.ExecuteReader();
        var tables = new List<string>();
        while (reader.Read())
            tables.Add(reader.GetString(0));

        Assert.Equal(
            [
                "ChangeCheckHistory", "ChangeWatermarks", "Invites", "Logs", "NotificationReadState",
                "Notifications", "PauseEvents", "RunLocks",
                "Sessions", "TaskRuns", "Tasks",
                "UserCredentials", "Users", "VerificationResults", "WorkQueue",
            ],
            tables);
    }

    [Fact]
    public void ReopeningExistingDatabase_DoesNotRerunMigrations()
    {
        var dbPath = Path.Combine(_tempDir, "state.db");
        var first = new StateDatabase(dbPath);
        var versionAfterFirstOpen = UserVersion(first);

        // A second StateDatabase against the same file must not fail by trying to CREATE TABLE again.
        var reopened = new StateDatabase(dbPath);

        // Asserted against the first open's own version rather than a hardcoded number, so that
        // adding a migration doesn't require editing this test to keep testing the same thing.
        Assert.True(versionAfterFirstOpen > 0, "Migrations should have been applied on first open.");
        Assert.Equal(versionAfterFirstOpen, UserVersion(reopened));
    }

    private static long UserVersion(StateDatabase database)
    {
        using var connection = database.OpenConnection();
        using var cmd = database.Command(connection, "PRAGMA user_version;");
        return Convert.ToInt64(cmd.ExecuteScalar());
    }

    [Fact]
    public void OpenConnection_SetsBusyTimeout()
    {
        // Not WAL mode — see the XML doc on StateDatabase.OpenConnection for why: WAL's cross-process
        // shared-memory coordination proved unreliable in this project's sandboxed dev environment.
        var database = new StateDatabase(Path.Combine(_tempDir, "state.db"));
        using var connection = database.OpenConnection();
        using var cmd = database.Command(connection, "PRAGMA busy_timeout;");
        Assert.Equal(5000L, Convert.ToInt64(cmd.ExecuteScalar()));
    }
}
