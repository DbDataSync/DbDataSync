namespace DataSync.State;

/// <summary>
/// Schema versioned via SQLite's built-in <c>PRAGMA user_version</c> — each entry is applied once,
/// in order, the first time a database is opened at a lower version. See
/// architecture/detailed-design.md §3.7 for the table rationale.
/// </summary>
internal static class Migrations
{
    public static readonly string[] Scripts =
    [
        """
        CREATE TABLE Tasks (
            Name TEXT PRIMARY KEY,
            Enabled INTEGER NOT NULL,
            UpdatedAtUtc TEXT NOT NULL
        );

        CREATE TABLE TaskRuns (
            RunId TEXT PRIMARY KEY,
            TaskName TEXT NOT NULL,
            Pid INTEGER NULL,
            Status TEXT NOT NULL,
            StartedAtUtc TEXT NOT NULL,
            EndedAtUtc TEXT NULL,
            RowsRead INTEGER NOT NULL DEFAULT 0,
            RowsWritten INTEGER NOT NULL DEFAULT 0,
            ErrorSummary TEXT NULL
        );
        CREATE INDEX IX_TaskRuns_TaskName ON TaskRuns(TaskName);

        CREATE TABLE ChangeWatermarks (
            TaskName TEXT NOT NULL,
            SourceTable TEXT NOT NULL,
            Cursor TEXT NOT NULL,
            UpdatedAtUtc TEXT NOT NULL,
            PRIMARY KEY (TaskName, SourceTable)
        );

        CREATE TABLE Logs (
            Id INTEGER PRIMARY KEY AUTOINCREMENT,
            RunId TEXT NOT NULL,
            TimestampUtc TEXT NOT NULL,
            Level TEXT NOT NULL,
            Message TEXT NOT NULL
        );
        CREATE INDEX IX_Logs_RunId ON Logs(RunId);

        CREATE TABLE RunLocks (
            TaskName TEXT PRIMARY KEY,
            RunId TEXT NOT NULL,
            AcquiredAtUtc TEXT NOT NULL
        );
        """,
    ];
}
