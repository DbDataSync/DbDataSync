using Microsoft.Data.Sqlite;

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
        SqliteRetry.Execute(() =>
        {
            using var connection = database.OpenConnection();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = """
                INSERT INTO VerificationResults
                    (RunId, TaskName, MappingName, CheckName, CompletedAtUtc, SourceReadAtUtc, TargetReadAtUtc,
                     GroupsCompared, DifferingGroups, ResultPath)
                VALUES ($runId, $task, $mapping, $check, $completed, $sourceRead, $targetRead,
                        $groups, $differing, $path)
                ON CONFLICT (RunId, CheckName) DO UPDATE SET
                    CompletedAtUtc = excluded.CompletedAtUtc,
                    SourceReadAtUtc = excluded.SourceReadAtUtc,
                    TargetReadAtUtc = excluded.TargetReadAtUtc,
                    GroupsCompared = excluded.GroupsCompared,
                    DifferingGroups = excluded.DifferingGroups,
                    ResultPath = excluded.ResultPath;
                """;
            cmd.Parameters.AddWithValue("$runId", result.RunId.ToString());
            cmd.Parameters.AddWithValue("$task", result.TaskName);
            cmd.Parameters.AddWithValue("$mapping", result.MappingName);
            cmd.Parameters.AddWithValue("$check", result.CheckName);
            cmd.Parameters.AddWithValue("$completed", result.CompletedAtUtc.ToString("O"));
            cmd.Parameters.AddWithValue("$sourceRead", result.SourceReadAtUtc.ToString("O"));
            cmd.Parameters.AddWithValue("$targetRead", result.TargetReadAtUtc.ToString("O"));
            cmd.Parameters.AddWithValue("$groups", result.GroupsCompared);
            cmd.Parameters.AddWithValue("$differing", result.DifferingGroups);
            cmd.Parameters.AddWithValue("$path", result.ResultPath);
            cmd.ExecuteNonQuery();
        });

    /// <summary>Most recent first, which is the order anybody asks in.</summary>
    public IReadOnlyList<VerificationResultRecord> List(string taskName, string? mappingName = null, int limit = 50) =>
        SqliteRetry.Execute(() =>
        {
            using var connection = database.OpenConnection();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = $"""
                SELECT Id, RunId, TaskName, MappingName, CheckName, CompletedAtUtc, SourceReadAtUtc,
                       TargetReadAtUtc, GroupsCompared, DifferingGroups, ResultPath
                FROM VerificationResults
                WHERE TaskName = $task {(mappingName is null ? "" : "AND MappingName = $mapping")}
                ORDER BY CompletedAtUtc DESC LIMIT $limit;
                """;
            cmd.Parameters.AddWithValue("$task", taskName);
            if (mappingName is not null)
                cmd.Parameters.AddWithValue("$mapping", mappingName);
            cmd.Parameters.AddWithValue("$limit", limit);

            using var reader = cmd.ExecuteReader();
            var results = new List<VerificationResultRecord>();
            while (reader.Read())
                results.Add(Read(reader));
            return (IReadOnlyList<VerificationResultRecord>)results;
        });

    public VerificationResultRecord? Get(long id) =>
        SqliteRetry.Execute(() =>
        {
            using var connection = database.OpenConnection();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = """
                SELECT Id, RunId, TaskName, MappingName, CheckName, CompletedAtUtc, SourceReadAtUtc,
                       TargetReadAtUtc, GroupsCompared, DifferingGroups, ResultPath
                FROM VerificationResults WHERE Id = $id;
                """;
            cmd.Parameters.AddWithValue("$id", id);

            using var reader = cmd.ExecuteReader();
            return reader.Read() ? Read(reader) : null;
        });

    private static VerificationResultRecord Read(SqliteDataReader reader) => new(
        reader.GetInt64(0),
        Guid.Parse(reader.GetString(1)),
        reader.GetString(2),
        reader.GetString(3),
        reader.GetString(4),
        DateTimeOffset.Parse(reader.GetString(5), null, System.Globalization.DateTimeStyles.RoundtripKind),
        DateTimeOffset.Parse(reader.GetString(6), null, System.Globalization.DateTimeStyles.RoundtripKind),
        DateTimeOffset.Parse(reader.GetString(7), null, System.Globalization.DateTimeStyles.RoundtripKind),
        reader.GetInt32(8),
        reader.GetInt32(9),
        reader.GetString(10));
}
