using System.Data.Common;

namespace DataSync.State;

/// <summary>
/// Where a verification result is, so the console can list and locate one without holding the result
/// itself — see phase 43. The parquet the TaskRunner wrote is the answer; this is the index.
/// </summary>
public sealed class VerificationResultStore(StateDatabase database)
{
    /// <summary>
    /// Idempotent on (run, check), because a run replayed from a state journal applies its outcomes
    /// again — every recovery operation has to be, and indexing the same file twice would be a
    /// duplicate row pointing at one result.
    /// </summary>
    public void Record(VerificationResultRecord result) =>
        database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            using var cmd = database.Command(connection, database.Dialect.Upsert(
                "VerificationResults",
                "RunId, TaskName, MappingName, CheckName, CompletedAtUtc, SourceReadAtUtc, TargetReadAtUtc, " +
                    "GroupsCompared, DifferingGroups, ResultPath",
                "$runId, $task, $mapping, $check, $completed, $sourceRead, $targetRead, " +
                    "$groups, $differing, $path",
                "RunId, CheckName",
                """
                CompletedAtUtc = EXCLUDED.CompletedAtUtc,
                SourceReadAtUtc = EXCLUDED.SourceReadAtUtc,
                TargetReadAtUtc = EXCLUDED.TargetReadAtUtc,
                GroupsCompared = EXCLUDED.GroupsCompared,
                DifferingGroups = EXCLUDED.DifferingGroups,
                ResultPath = EXCLUDED.ResultPath
                """));
            cmd.Bind(database, "runId", result.RunId.ToString());
            cmd.Bind(database, "task", result.TaskName);
            cmd.Bind(database, "mapping", result.MappingName);
            cmd.Bind(database, "check", result.CheckName);
            cmd.Bind(database, "completed", result.CompletedAtUtc.ToString("O"));
            cmd.Bind(database, "sourceRead", result.SourceReadAtUtc.ToString("O"));
            cmd.Bind(database, "targetRead", result.TargetReadAtUtc.ToString("O"));
            cmd.Bind(database, "groups", result.GroupsCompared);
            cmd.Bind(database, "differing", result.DifferingGroups);
            cmd.Bind(database, "path", result.ResultPath);
            cmd.ExecuteNonQuery();
        });

    /// <summary>Most recent first, which is the order anybody asks in.</summary>
    public IReadOnlyList<VerificationResultRecord> List(string taskName, string? mappingName = null, int limit = 50) =>
        database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            using var cmd = database.Command(connection, $"""
                SELECT Id, RunId, TaskName, MappingName, CheckName, CompletedAtUtc, SourceReadAtUtc,
                       TargetReadAtUtc, GroupsCompared, DifferingGroups, ResultPath
                FROM VerificationResults
                WHERE TaskName = $task {(mappingName is null ? "" : "AND MappingName = $mapping")}
                ORDER BY CompletedAtUtc DESC {database.Limit("limit")};
                """);
            cmd.Bind(database, "task", taskName);
            if (mappingName is not null)
                cmd.Bind(database, "mapping", mappingName);
            cmd.Bind(database, "limit", limit);

            using var reader = cmd.ExecuteReader();
            var results = new List<VerificationResultRecord>();
            while (reader.Read())
                results.Add(Read(reader));
            return (IReadOnlyList<VerificationResultRecord>)results;
        });

    public VerificationResultRecord? Get(long id) =>
        database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            using var cmd = database.Command(connection, """
                SELECT Id, RunId, TaskName, MappingName, CheckName, CompletedAtUtc, SourceReadAtUtc,
                       TargetReadAtUtc, GroupsCompared, DifferingGroups, ResultPath
                FROM VerificationResults WHERE Id = $id;
                """);
            cmd.Bind(database, "id", id);

            using var reader = cmd.ExecuteReader();
            return reader.Read() ? Read(reader) : null;
        });

    /// <summary>
    /// Forgets one result. The caller deletes the file — this store owns the index, not the artifact,
    /// and a store that reached out to the filesystem would be two responsibilities in one place.
    /// </summary>
    /// <returns>False when there was no such row, so a double-delete reads as "already gone" rather
    /// than as a failure.</returns>
    public bool Delete(long id) =>
        database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            using var cmd = database.Command(connection, "DELETE FROM VerificationResults WHERE Id = $id;");
            cmd.Bind(database, "id", id);
            return cmd.ExecuteNonQuery() == 1;
        });

    private static VerificationResultRecord Read(DbDataReader reader) => new(
        reader.Int64(0),
        Guid.Parse(reader.GetString(1)),
        reader.GetString(2),
        reader.GetString(3),
        reader.GetString(4),
        DateTimeOffset.Parse(reader.GetString(5), null, System.Globalization.DateTimeStyles.RoundtripKind),
        DateTimeOffset.Parse(reader.GetString(6), null, System.Globalization.DateTimeStyles.RoundtripKind),
        DateTimeOffset.Parse(reader.GetString(7), null, System.Globalization.DateTimeStyles.RoundtripKind),
        reader.Int32(8),
        reader.Int32(9),
        reader.GetString(10));
}
