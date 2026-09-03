using System.Data.Common;

namespace DbDataSync.State;

/// <summary>
/// Prevents overlapping runs of the same (task, run kind, table mapping) — architecture/detailed-design.md
/// §3.1, §3.7, and architecture/implementation/done/phase-008-work-queue-schema.md for why this is scoped per
/// mapping rather than per replication: a Primary pass is now "this mapping's next incremental pass,"
/// not "this replication's next incremental pass over every mapping," so one slow mapping no longer
/// blocks every other mapping's schedule.
/// </summary>
public sealed class RunLockStore(StateDatabase database)
{
    public bool TryAcquire(string taskName, RunKind runKind, string mappingName, Guid runId) =>
        database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            using var cmd = database.Command(connection, database.Dialect.InsertOrIgnore(
                "RunLocks",
                "TaskName, RunKind, MappingName, RunId, AcquiredAtUtc",
                "$task, $kind, $mapping, $runId, $now",
                "TaskName, RunKind, MappingName"));
            cmd.Bind(database, "task", taskName);
            cmd.Bind(database, "kind", runKind.ToString());
            cmd.Bind(database, "mapping", mappingName);
            cmd.Bind(database, "runId", runId.ToString());
            cmd.Bind(database, "now", DateTimeOffset.UtcNow.ToString("O"));
            return cmd.ExecuteNonQuery() == 1;
        });

    public void Release(string taskName, RunKind runKind, string mappingName) =>
        database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            using var cmd = database.Command(connection, "DELETE FROM RunLocks WHERE TaskName = $task AND RunKind = $kind AND MappingName = $mapping;");
            cmd.Bind(database, "task", taskName);
            cmd.Bind(database, "kind", runKind.ToString());
            cmd.Bind(database, "mapping", mappingName);
            cmd.ExecuteNonQuery();
        });

    public bool IsLocked(string taskName, RunKind runKind, string mappingName) =>
        database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            using var cmd = database.Command(connection, "SELECT COUNT(*) FROM RunLocks WHERE TaskName = $task AND RunKind = $kind AND MappingName = $mapping;");
            cmd.Bind(database, "task", taskName);
            cmd.Bind(database, "kind", runKind.ToString());
            cmd.Bind(database, "mapping", mappingName);
            return Convert.ToInt64(cmd.ExecuteScalar()) > 0;
        });
}
