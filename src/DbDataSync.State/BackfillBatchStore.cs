using System.Data.Common;

namespace DbDataSync.State;

/// <summary>Where a backfill is, taken as a whole rather than one segment at a time.</summary>
public enum BackfillState
{
    /// <summary>At least one segment is still queued or running.</summary>
    Running,

    /// <summary>Every segment finished, and every one that finished succeeded.</summary>
    Completed,

    /// <summary>Every segment finished, and at least one failed.</summary>
    CompletedWithFailures,
}

/// <param name="SegmentCount">How many segments this backfill was split into, recorded at enqueue —
/// not <c>COUNT</c> of the runs, which can undercount if an equivalent segment was already in flight
/// when this batch was queued.</param>
/// <param name="RowsCopied">Rows written to the target across the segments that have finished. Segment
/// runs write their totals only on completion, so this steps up per segment rather than streaming.</param>
/// <param name="EstimatedRows">One catalog-statistics estimate for the whole source table, read once
/// at enqueue. Null when there was no table to estimate (a query source) or the driver has no catalog
/// to read.</param>
/// <param name="EstimateCaveat">Why the estimate should be read loosely, if it should — e.g.
/// <c>"ignores row filter"</c> when the mapping narrows its source but the estimate counts the whole
/// table. Null when the estimate is clean.</param>
public sealed record BackfillBatchProgress(
    string BatchId,
    string MappingName,
    DateTimeOffset CreatedAtUtc,
    int SegmentCount,
    int SegmentsSucceeded,
    int SegmentsFailed,
    int SegmentsRunning,
    long RowsRead,
    long RowsCopied,
    long? EstimatedRows,
    string? EstimateCaveat,
    DateTimeOffset? StartedAtUtc,
    DateTimeOffset? LastActivityUtc)
{
    public BackfillState State =>
        SegmentsSucceeded + SegmentsFailed < SegmentCount ? BackfillState.Running
        : SegmentsFailed > 0 ? BackfillState.CompletedWithFailures
        : BackfillState.Completed;
}

/// <summary>
/// The batch view over a backfill's segment runs. <c>BackfillBatches</c> holds what a segment doesn't
/// carry — the planned segment count and one whole-table row estimate — and the per-segment progress
/// is aggregated from <c>TaskRuns</c>, which already records every segment's status and final row
/// counts. This is the query nobody had written, the same way <see cref="RunMetricsStore"/> was.
/// </summary>
public sealed class BackfillBatchStore(StateDatabase database)
{
    public void CreateBatch(
        string batchId, string taskName, string mappingName, int segmentCount,
        long? estimatedRows, string? estimateCaveat) =>
        database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            using var cmd = database.Command(connection, """
                INSERT INTO BackfillBatches
                    (BatchId, TaskName, MappingName, CreatedAtUtc, SegmentCount, EstimatedRows, EstimateCaveat)
                VALUES ($batchId, $taskName, $mappingName, $createdAt, $segmentCount, $estimatedRows, $estimateCaveat);
                """);
            cmd.Bind(database, "batchId", batchId);
            cmd.Bind(database, "taskName", taskName);
            cmd.Bind(database, "mappingName", mappingName);
            cmd.Bind(database, "createdAt", DateTimeOffset.UtcNow.ToString("O"));
            cmd.Bind(database, "segmentCount", segmentCount);
            cmd.Bind(database, "estimatedRows", (object?)estimatedRows ?? DBNull.Value);
            cmd.Bind(database, "estimateCaveat", (object?)estimateCaveat ?? DBNull.Value);
            cmd.ExecuteNonQuery();
        });

    /// <summary>
    /// The most recent backfills for one replication, newest first — one row per batch with its
    /// segments rolled up. The Monitoring card reads only the first; the wider list is what a future
    /// Batch Load History screen is for, which is why this takes a limit rather than returning one.
    /// <para>
    /// Timestamps compare as text, correct because every writer stores
    /// <c>DateTimeOffset.UtcNow.ToString("O")</c> — the assumption the rest of this store makes too.
    /// </para>
    /// </summary>
    public IReadOnlyList<BackfillBatchProgress> GetRecentBackfills(string taskName, int limit) =>
        database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            using var cmd = database.Command(connection, $"""
                SELECT b.BatchId, b.MappingName, b.CreatedAtUtc, b.SegmentCount,
                       b.EstimatedRows, b.EstimateCaveat,
                       SUM(CASE WHEN r.Status = 'Succeeded' THEN 1 ELSE 0 END) AS SegmentsSucceeded,
                       SUM(CASE WHEN r.Status = 'Failed'    THEN 1 ELSE 0 END) AS SegmentsFailed,
                       SUM(CASE WHEN r.Status = 'Running'   THEN 1 ELSE 0 END) AS SegmentsRunning,
                       SUM(r.RowsRead)     AS RowsRead,
                       SUM(r.RowsWritten)  AS RowsCopied,
                       MIN(r.StartedAtUtc) AS StartedAtUtc,
                       MAX(r.EndedAtUtc)   AS LastActivityUtc
                FROM BackfillBatches b
                LEFT JOIN TaskRuns r ON r.BackfillBatchId = b.BatchId
                WHERE b.TaskName = $taskName
                GROUP BY b.BatchId, b.MappingName, b.CreatedAtUtc, b.SegmentCount,
                         b.EstimatedRows, b.EstimateCaveat
                ORDER BY b.CreatedAtUtc DESC {database.Limit("limit")};
                """);
            cmd.Bind(database, "taskName", taskName);
            cmd.Bind(database, "limit", limit);

            using var reader = cmd.ExecuteReader();
            var results = new List<BackfillBatchProgress>();
            while (reader.Read())
            {
                results.Add(new BackfillBatchProgress(
                    BatchId: reader.GetString(0),
                    MappingName: reader.GetString(1),
                    CreatedAtUtc: DateTimeOffset.Parse(reader.GetString(2)),
                    SegmentCount: reader.Int32(3),
                    SegmentsSucceeded: reader.IsDBNull(6) ? 0 : reader.Int32(6),
                    SegmentsFailed: reader.IsDBNull(7) ? 0 : reader.Int32(7),
                    SegmentsRunning: reader.IsDBNull(8) ? 0 : reader.Int32(8),
                    RowsRead: reader.IsDBNull(9) ? 0 : reader.Int64(9),
                    RowsCopied: reader.IsDBNull(10) ? 0 : reader.Int64(10),
                    EstimatedRows: reader.NullableInt64(4),
                    EstimateCaveat: reader.IsDBNull(5) ? null : reader.GetString(5),
                    StartedAtUtc: reader.IsDBNull(11) ? null : DateTimeOffset.Parse(reader.GetString(11)),
                    LastActivityUtc: reader.IsDBNull(12) ? null : DateTimeOffset.Parse(reader.GetString(12))));
            }
            return (IReadOnlyList<BackfillBatchProgress>)results;
        });
}
