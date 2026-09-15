using System.Data.Common;

namespace DbDataSync.State;

/// <summary>Where a bulk load is, taken as a whole rather than one segment at a time.</summary>
public enum BulkLoadState
{
    /// <summary>At least one segment is still queued or running.</summary>
    Running,

    /// <summary>Every segment finished, and every one that finished succeeded.</summary>
    Completed,

    /// <summary>Every segment finished, and at least one failed.</summary>
    CompletedWithFailures,
}

/// <param name="SegmentCount">How many segments this bulk load was split into, recorded at enqueue —
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
public sealed record BulkLoadBatchProgress(
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
    public BulkLoadState State =>
        SegmentsSucceeded + SegmentsFailed < SegmentCount ? BulkLoadState.Running
        : SegmentsFailed > 0 ? BulkLoadState.CompletedWithFailures
        : BulkLoadState.Completed;
}

/// <summary>
/// A position in <see cref="BulkLoadBatchStore.GetHistory"/>'s keyset — mirrors <c>RunHistoryCursor</c>
/// (phase 104), see phase 139.
/// <para>
/// <c>(CreatedAtUtc, BatchId)</c>, not an offset, for the same reason <c>RunHistoryCursor</c> isn't
/// one: the history is append-heavy, so an offset would shift under a page as new batches land.
/// <c>BatchId</c> is a <c>Guid.NewGuid().ToString("N")</c> stamped once at enqueue — not a meaningful
/// order, only a deterministic (ordinary string comparison) tiebreak for two batches created in the
/// same instant, the same role <c>RunId</c> plays for <c>RunHistoryCursor</c> despite being just as
/// arbitrary an order there.
/// </para>
/// </summary>
public readonly record struct BulkLoadHistoryCursor(DateTimeOffset CreatedAtUtc, string BatchId);

/// <summary>
/// One page of <see cref="BulkLoadBatchStore.GetHistory"/>, and where the next one starts — mirrors
/// <c>RunHistoryPage</c>. Unlike that type this does not implement <c>IReadOnlyList</c> itself: every
/// caller of <c>GetHistory</c> is the new history endpoint, so there is no pre-paging call site to keep
/// compiling unaware a next page exists.
/// </summary>
public sealed record BulkLoadHistoryPage(IReadOnlyList<BulkLoadBatchProgress> Batches, BulkLoadHistoryCursor? NextCursor);

/// <summary>
/// The batch view over a bulk load's segment runs. <c>BulkLoadBatches</c> holds what a segment doesn't
/// carry — the planned segment count and one whole-table row estimate — and the per-segment progress
/// is aggregated from <c>TaskRuns</c>, which already records every segment's status and final row
/// counts. This is the query nobody had written, the same way <see cref="RunMetricsStore"/> was.
/// </summary>
public sealed class BulkLoadBatchStore(StateDatabase database)
{
    public void CreateBatch(
        string batchId, string taskName, string mappingName, int segmentCount,
        long? estimatedRows, string? estimateCaveat) =>
        database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            using var cmd = database.Command(connection, """
                INSERT INTO BulkLoadBatches
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
    /// One batch's rolled-up state, by id alone — a batch id is already globally unique, so there is no
    /// need for the caller to know which replication it belongs to. Used by
    /// <c>LocalRunnerState.CompleteRun</c> (phase 134) to decide whether a just-completed segment run
    /// finished the batch a mapping's pending initial load is waiting on.
    /// </summary>
    public BulkLoadBatchProgress? GetBatch(string batchId) =>
        database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            using var cmd = database.Command(connection, """
                SELECT b.BatchId, b.MappingName, b.CreatedAtUtc, b.SegmentCount,
                       b.EstimatedRows, b.EstimateCaveat,
                       SUM(CASE WHEN r.Status = 'Succeeded' THEN 1 ELSE 0 END) AS SegmentsSucceeded,
                       SUM(CASE WHEN r.Status = 'Failed'    THEN 1 ELSE 0 END) AS SegmentsFailed,
                       SUM(CASE WHEN r.Status = 'Running'   THEN 1 ELSE 0 END) AS SegmentsRunning,
                       SUM(r.RowsRead)     AS RowsRead,
                       SUM(r.RowsWritten)  AS RowsCopied,
                       MIN(r.StartedAtUtc) AS StartedAtUtc,
                       MAX(r.EndedAtUtc)   AS LastActivityUtc
                FROM BulkLoadBatches b
                LEFT JOIN TaskRuns r ON r.BulkLoadBatchId = b.BatchId
                WHERE b.BatchId = $batchId
                GROUP BY b.BatchId, b.MappingName, b.CreatedAtUtc, b.SegmentCount,
                         b.EstimatedRows, b.EstimateCaveat;
                """);
            cmd.Bind(database, "batchId", batchId);

            using var reader = cmd.ExecuteReader();
            if (!reader.Read())
                return null;

            return new BulkLoadBatchProgress(
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
                LastActivityUtc: reader.IsDBNull(12) ? null : DateTimeOffset.Parse(reader.GetString(12)));
        });

    /// <summary>
    /// The most recent bulk loads for one replication, newest first — one row per batch with its
    /// segments rolled up. The Monitoring card reads only the first; the wider list is what a future
    /// Batch Load History screen is for, which is why this takes a limit rather than returning one.
    /// <para>
    /// Timestamps compare as text, correct because every writer stores
    /// <c>DateTimeOffset.UtcNow.ToString("O")</c> — the assumption the rest of this store makes too.
    /// </para>
    /// </summary>
    public IReadOnlyList<BulkLoadBatchProgress> GetRecentBulkLoads(string taskName, int limit) =>
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
                FROM BulkLoadBatches b
                LEFT JOIN TaskRuns r ON r.BulkLoadBatchId = b.BatchId
                WHERE b.TaskName = $taskName
                GROUP BY b.BatchId, b.MappingName, b.CreatedAtUtc, b.SegmentCount,
                         b.EstimatedRows, b.EstimateCaveat
                ORDER BY b.CreatedAtUtc DESC {database.Limit("limit")};
                """);
            cmd.Bind(database, "taskName", taskName);
            cmd.Bind(database, "limit", limit);

            using var reader = cmd.ExecuteReader();
            var results = new List<BulkLoadBatchProgress>();
            while (reader.Read())
            {
                results.Add(new BulkLoadBatchProgress(
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
            return (IReadOnlyList<BulkLoadBatchProgress>)results;
        });

    /// <summary>
    /// Bulk-load history for one replication — filtered and keyset-paged, see phase 139. A new method
    /// beside <see cref="GetRecentBulkLoads"/> rather than a generalization of it: that one keeps the
    /// Monitoring card's own contract (newest one, no filter, no cursor) exactly as it is.
    /// <para>
    /// The same <c>SELECT</c>/<c>GROUP BY</c> shape as <see cref="GetRecentBulkLoads"/> and
    /// <see cref="GetBatch"/>, with an optional <c>MappingName</c> equality filter and the keyset
    /// predicate added ahead of the <c>GROUP BY</c> — a keyset condition on <c>BulkLoadBatches</c>' own
    /// columns has to filter which of its rows enter the aggregation, not which aggregated groups come
    /// out of it, so it belongs in this <c>WHERE</c> rather than a <c>HAVING</c>.
    /// </para>
    /// <para>
    /// The keyset predicate is written the expanded way, matching <c>TaskRunStore.GetRunHistory</c> —
    /// <c>CreatedAtUtc &lt; $cursorTime OR (CreatedAtUtc = $cursorTime AND BatchId &lt; $cursorBatchId)</c>
    /// — rather than the row-value-constructor form, which SQL Server does not support and this store
    /// runs on all three engines (phase 63).
    /// </para>
    /// <para>
    /// One row over <paramref name="limit"/> is fetched, not a second <c>COUNT</c> query, so "is there
    /// a next page" is answered by what already came back — the extra row is trimmed before it reaches
    /// a caller, and its own key becomes <see cref="BulkLoadHistoryPage.NextCursor"/>.
    /// </para>
    /// </summary>
    public BulkLoadHistoryPage GetHistory(
        string taskName, string? mappingName = null, BulkLoadHistoryCursor? cursor = null, int limit = 20) =>
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
                FROM BulkLoadBatches b
                LEFT JOIN TaskRuns r ON r.BulkLoadBatchId = b.BatchId
                WHERE b.TaskName = $taskName
                {(mappingName is null ? "" : "AND b.MappingName = $mappingName")}
                {(cursor is null ? "" : "AND (b.CreatedAtUtc < $cursorTime OR (b.CreatedAtUtc = $cursorTime AND b.BatchId < $cursorBatchId))")}
                GROUP BY b.BatchId, b.MappingName, b.CreatedAtUtc, b.SegmentCount,
                         b.EstimatedRows, b.EstimateCaveat
                ORDER BY b.CreatedAtUtc DESC, b.BatchId DESC
                {database.Limit("fetchLimit")};
                """);
            cmd.Bind(database, "taskName", taskName);
            if (mappingName is not null)
                cmd.Bind(database, "mappingName", mappingName);
            if (cursor is not null)
            {
                cmd.Bind(database, "cursorTime", cursor.Value.CreatedAtUtc.ToString("O"));
                cmd.Bind(database, "cursorBatchId", cursor.Value.BatchId);
            }
            // One more than asked for — see the doc comment above.
            cmd.Bind(database, "fetchLimit", limit + 1);

            using var reader = cmd.ExecuteReader();
            var results = new List<BulkLoadBatchProgress>();
            while (reader.Read())
            {
                results.Add(new BulkLoadBatchProgress(
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

            BulkLoadHistoryCursor? next = null;
            if (results.Count > limit)
            {
                results.RemoveAt(results.Count - 1);
                var last = results[^1];
                next = new BulkLoadHistoryCursor(last.CreatedAtUtc, last.BatchId);
            }

            return new BulkLoadHistoryPage(results, next);
        });
}
