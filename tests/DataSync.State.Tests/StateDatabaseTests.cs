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
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table' AND name != 'sqlite_sequence' ORDER BY name;";
        using var reader = cmd.ExecuteReader();
        var tables = new List<string>();
        while (reader.Read())
            tables.Add(reader.GetString(0));

        Assert.Equal(["ChangeWatermarks", "Logs", "RunLocks", "TaskRuns", "Tasks"], tables);
    }

    [Fact]
    public void ReopeningExistingDatabase_DoesNotRerunMigrations()
    {
        var dbPath = Path.Combine(_tempDir, "state.db");
        _ = new StateDatabase(dbPath);

        // A second StateDatabase against the same file must not fail by trying to CREATE TABLE again.
        var reopened = new StateDatabase(dbPath);

        using var connection = reopened.OpenConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "PRAGMA user_version;";
        Assert.Equal(1L, Convert.ToInt64(cmd.ExecuteScalar()));
    }

    [Fact]
    public void OpenConnection_EnablesWalMode()
    {
        var database = new StateDatabase(Path.Combine(_tempDir, "state.db"));
        using var connection = database.OpenConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "PRAGMA journal_mode;";
        Assert.Equal("wal", Convert.ToString(cmd.ExecuteScalar())?.ToLowerInvariant());
    }
}
