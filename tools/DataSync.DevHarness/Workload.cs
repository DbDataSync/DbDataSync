using System.Diagnostics;
using Microsoft.Data.SqlClient;

namespace DataSync.DevHarness;

/// <summary>
/// A steady stream of insert/update/delete transactions against the source, so incremental sync can
/// be watched keeping up with a table that is actually moving — rather than the two-row, one-shot
/// shape every automated test in this repo uses.
/// </summary>
public static class Workload
{
    public static async Task RunAsync(int ratePerSecond, TimeSpan duration, CancellationToken cancellationToken)
    {
        if (ratePerSecond < 1)
            throw new HarnessException("--rate must be at least 1 transaction per second.");

        await using var connection = await SqlBootstrap.OpenAsync(
            Scenario.SourceConnectionString(Scenario.DatabaseName), cancellationToken);

        var nextId = await SqlBootstrap.MaxIdAsync(connection, cancellationToken) + 1;
        var liveIds = await LoadLiveIdsAsync(connection, cancellationToken);
        if (liveIds.Count == 0)
            Log.Warn("the source table is empty — the first transactions will all be inserts. `seed` first for a fuller mix.");

        Log.Step($"Running {ratePerSecond}/s for {Describe(duration)} (Ctrl+C to stop early)");

        var random = new Random();
        var interval = TimeSpan.FromSeconds(1.0 / ratePerSecond);
        var deadline = Stopwatch.StartNew();
        var counts = new Counts();
        var lastReport = TimeSpan.Zero;

        try
        {
            while (deadline.Elapsed < duration && !cancellationToken.IsCancellationRequested)
            {
                var tickStart = deadline.Elapsed;

                // Inserts stay in the majority so the table grows over a long run, but updates and
                // deletes are frequent enough that the reader has all three operations to carry and a
                // merge writer has something to do beyond appending.
                var roll = random.NextDouble();
                if (liveIds.Count == 0 || roll < 0.5)
                {
                    await InsertAsync(connection, nextId, random, cancellationToken);
                    liveIds.Add(nextId++);
                    counts.Inserted++;
                }
                else if (roll < 0.85)
                {
                    await UpdateAsync(connection, liveIds[random.Next(liveIds.Count)], random, cancellationToken);
                    counts.Updated++;
                }
                else
                {
                    var index = random.Next(liveIds.Count);
                    await DeleteAsync(connection, liveIds[index], cancellationToken);
                    // Swap-remove: order doesn't matter here and it keeps this O(1) at large sizes.
                    liveIds[index] = liveIds[^1];
                    liveIds.RemoveAt(liveIds.Count - 1);
                    counts.Deleted++;
                }

                if (deadline.Elapsed - lastReport > TimeSpan.FromSeconds(5))
                {
                    lastReport = deadline.Elapsed;
                    Log.Info($"{counts} ({liveIds.Count:N0} live row(s))");
                }

                var elapsed = deadline.Elapsed - tickStart;
                if (elapsed < interval)
                    await Task.Delay(interval - elapsed, cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            Log.Info("stopping early");
        }

        Log.Ok($"{counts} over {Describe(deadline.Elapsed)} — source now holds {liveIds.Count:N0} row(s)");
    }

    private sealed class Counts
    {
        public int Inserted;
        public int Updated;
        public int Deleted;

        public override string ToString() => $"{Inserted:N0} inserted, {Updated:N0} updated, {Deleted:N0} deleted";
    }

    /// <summary>
    /// Keys are held in memory for the run rather than re-queried each tick: picking an update or
    /// delete target is the hot path, and a round trip per transaction just to choose one would cap
    /// the achievable rate well below what the writer side can absorb.
    /// </summary>
    private static async Task<List<int>> LoadLiveIdsAsync(SqlConnection connection, CancellationToken cancellationToken)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = $"SELECT Id FROM {Scenario.QualifiedTable};";

        var ids = new List<int>();
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            ids.Add(reader.GetInt32(0));
        return ids;
    }

    private static async Task InsertAsync(SqlConnection connection, int id, Random random, CancellationToken cancellationToken)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = $"""
            INSERT INTO {Scenario.QualifiedTable} (Id, Region, CustomerName, Amount, UpdatedAtUtc)
            VALUES (@id, @region, @name, @amount, @updated);
            """;
        cmd.Parameters.AddWithValue("@id", id);
        cmd.Parameters.AddWithValue("@region", Scenario.Regions[random.Next(Scenario.Regions.Length)]);
        cmd.Parameters.AddWithValue("@name", $"Customer {id:D6}");
        cmd.Parameters.AddWithValue("@amount", Math.Round((decimal)(random.NextDouble() * 5000), 2));
        cmd.Parameters.AddWithValue("@updated", DateTime.UtcNow);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task UpdateAsync(SqlConnection connection, int id, Random random, CancellationToken cancellationToken)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = $"""
            UPDATE {Scenario.QualifiedTable}
            SET Amount = @amount, Region = @region, UpdatedAtUtc = @updated
            WHERE Id = @id;
            """;
        cmd.Parameters.AddWithValue("@id", id);
        cmd.Parameters.AddWithValue("@region", Scenario.Regions[random.Next(Scenario.Regions.Length)]);
        cmd.Parameters.AddWithValue("@amount", Math.Round((decimal)(random.NextDouble() * 5000), 2));
        cmd.Parameters.AddWithValue("@updated", DateTime.UtcNow);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task DeleteAsync(SqlConnection connection, int id, CancellationToken cancellationToken)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = $"DELETE FROM {Scenario.QualifiedTable} WHERE Id = @id;";
        cmd.Parameters.AddWithValue("@id", id);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    public static string Describe(TimeSpan duration) =>
        duration.TotalMinutes >= 1 ? $"{duration.TotalMinutes:0.#}m" : $"{duration.TotalSeconds:0.#}s";
}
