using Microsoft.Data.SqlClient;

namespace DataSync.DevHarness;

/// <summary>
/// Corrupts the *target* directly, behind the replication's back.
/// <para>
/// This is the one failure an incremental reader structurally cannot fix: nothing changed at the
/// source, so Change Tracking has nothing to report, and the next incremental pass will read zero
/// rows and consider itself correct forever. Repairing it needs a reload that re-reads the source and
/// makes the target match — which is the whole reason batch reload exists, and the fastest way to see
/// it work.
/// </para>
/// </summary>
public static class Drift
{
    public static async Task InjectAsync(int rows, CancellationToken cancellationToken)
    {
        await using var target = await SqlBootstrap.OpenAsync(
            Scenario.TargetConnectionString(Scenario.DatabaseName), cancellationToken);
        await using var source = await SqlBootstrap.OpenAsync(
            Scenario.SourceConnectionString(Scenario.DatabaseName), cancellationToken);

        var total = await SqlBootstrap.CountAsync(target, cancellationToken);
        if (total == 0)
            throw new HarnessException("The target table is empty — run a replication first, so there's something to corrupt.");

        var each = Math.Max(1, rows / 3);
        Log.Step($"Injecting drift into the target ({each} deleted, {each} altered, {each} phantom rows)");

        var deleted = await DeleteRowsAsync(target, each, cancellationToken);
        var altered = await AlterRowsAsync(target, each, cancellationToken);
        var phantomIds = await InsertPhantomsAsync(target, source, each, cancellationToken);

        Log.Ok($"{deleted} row(s) deleted, {altered} altered, {phantomIds.Count} phantom row(s) inserted");
        Log.Info("`verify` will now report differences; an incremental run will not fix any of them.");
        Log.Info($"Repair with a backfill of '{Scenario.MappingName}' using a reconciling writer.");

        if (phantomIds.Count > 0)
        {
            var inRange = await AreWithinSourceRangeAsync(source, phantomIds, cancellationToken);
            Log.Info(inRange
                ? $"Phantom Ids {phantomIds[0]}–{phantomIds[^1]} sit inside the source's key range, so a " +
                  "segmented reload covering them removes them too."
                : $"Phantom Ids {phantomIds[0]}–{phantomIds[^1]} sit *outside* the source's key range (it has no " +
                  "gaps to use). An Auto-segmented reload derives its buckets from the source's own MIN/MAX, so " +
                  "it will not reach them — only a Full segment will.");
        }
    }

    private static async Task<int> DeleteRowsAsync(SqlConnection connection, int count, CancellationToken cancellationToken)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = $"DELETE TOP (@count) FROM {Scenario.QualifiedTable};";
        cmd.Parameters.AddWithValue("@count", count);
        return await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>Alters values rather than keys, so the rows still *match* on the primary key and the
    /// difference is a wrong value — the kind a row-count check would miss entirely.</summary>
    private static async Task<int> AlterRowsAsync(SqlConnection connection, int count, CancellationToken cancellationToken)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = $"""
            UPDATE TOP (@count) {Scenario.QualifiedTable}
            SET CustomerName = CONCAT('DRIFTED ', CustomerName), Amount = -1;
            """;
        cmd.Parameters.AddWithValue("@count", count);
        return await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>
    /// Inserts rows into the target that the source has never had, preferring keys that fall in *gaps*
    /// within the source's own key range.
    /// <para>
    /// Placement matters more than it looks. An Auto-segmented reload derives its buckets from the
    /// source's real MIN/MAX, so a phantom above that range falls outside every bucket and a
    /// segment-scoped reconcile correctly refuses to touch it — which makes the obvious
    /// drift-then-backfill-then-verify loop appear not to converge when the product is behaving
    /// exactly as designed. Using gaps keeps phantoms reachable by any reload that covers their range.
    /// </para>
    /// </summary>
    private static async Task<List<int>> InsertPhantomsAsync(
        SqlConnection target, SqlConnection source, int count, CancellationToken cancellationToken)
    {
        var ids = await FindGapIdsAsync(source, count, cancellationToken);

        // No gaps (a freshly seeded, contiguous table). Fall back to keys above the source's range —
        // still valid drift, just only reachable by a reload whose segment covers them, which the
        // caller is told about.
        if (ids.Count < count)
        {
            var next = await SqlBootstrap.MaxIdAsync(target, cancellationToken) + 1;
            while (ids.Count < count)
                ids.Add(next++);
        }

        foreach (var id in ids)
        {
            await using var cmd = target.CreateCommand();
            cmd.CommandText = $"""
                INSERT INTO {Scenario.QualifiedTable} (Id, Region, CustomerName, Amount, UpdatedAtUtc)
                VALUES (@id, 'EU', @name, 0, @updated);
                """;
            cmd.Parameters.AddWithValue("@id", id);
            cmd.Parameters.AddWithValue("@name", $"PHANTOM {id}");
            cmd.Parameters.AddWithValue("@updated", DateTime.UtcNow);
            await cmd.ExecuteNonQueryAsync(cancellationToken);
        }

        ids.Sort();
        return ids;
    }

    /// <summary>Keys the source does not have but which lie between keys it does — found with a
    /// window function so this costs one round trip rather than pulling the key set over.</summary>
    private static async Task<List<int>> FindGapIdsAsync(SqlConnection source, int count, CancellationToken cancellationToken)
    {
        await using var cmd = source.CreateCommand();
        cmd.CommandText = $"""
            SELECT TOP (@count) Id + 1
            FROM (SELECT Id, LEAD(Id) OVER (ORDER BY Id) AS NextId FROM {Scenario.QualifiedTable}) gaps
            WHERE NextId > Id + 1
            ORDER BY Id;
            """;
        cmd.Parameters.AddWithValue("@count", count);

        var ids = new List<int>();
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            ids.Add(reader.GetInt32(0));
        return ids;
    }

    private static async Task<bool> AreWithinSourceRangeAsync(
        SqlConnection source, List<int> ids, CancellationToken cancellationToken)
    {
        await using var cmd = source.CreateCommand();
        cmd.CommandText = $"SELECT ISNULL(MIN(Id), 0), ISNULL(MAX(Id), 0) FROM {Scenario.QualifiedTable};";
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            return false;

        var (min, max) = (reader.GetInt32(0), reader.GetInt32(1));
        return ids[0] >= min && ids[^1] <= max;
    }
}
