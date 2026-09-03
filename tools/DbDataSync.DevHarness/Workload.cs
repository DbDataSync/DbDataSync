using System.Diagnostics;
using Microsoft.Data.SqlClient;

namespace DbDataSync.DevHarness;

/// <summary>
/// A steady stream of insert/update/delete transactions against the source, so incremental sync can
/// be watched keeping up with tables that are actually moving — rather than the two-row, one-shot
/// shape every automated test in this repo uses.
/// <para>
/// **Concurrent, not interleaved.** The tables are split into <c>--parallelism P</c> groups and each
/// group runs its own loop on its own <see cref="SqlConnection"/>, all P at once. One connection
/// issuing one statement at a time is sequential no matter which table it happens to be aiming at, so
/// P simultaneous connections is the only thing that produces the "several mappings changing at once"
/// scenario this exists for.
/// </para>
/// <para>
/// **Within a group, tables are visited in a fixed rotation** — one table per turn, cycling back to
/// the start. A round-robin rather than a random pick because it is what makes per-table distribution
/// actually even: over a short run a random pick can favour some tables purely by chance, and a table
/// that went quiet by accident looks exactly like a table that went quiet because of a bug.
/// </para>
/// </summary>
public static class Workload
{
    public static async Task RunAsync(
        int ratePerSecond,
        TimeSpan duration,
        IReadOnlyList<HarnessTable> tables,
        int parallelism,
        CancellationToken cancellationToken)
    {
        if (ratePerSecond < 1)
            throw new HarnessException("--rate must be at least 1 transaction per second.");
        if (parallelism < 1)
            throw new HarnessException("--parallelism must be at least 1.");
        if (parallelism > tables.Count)
            throw new HarnessException(
                $"--parallelism {parallelism} exceeds --tables {tables.Count}; a group with no tables has nothing to do.");

        var groups = Split(tables, parallelism);
        Log.Step(
            $"Running {ratePerSecond}/s across {tables.Count} table(s) on {groups.Count} concurrent " +
            $"connection(s) for {Describe(duration)} (Ctrl+C to stop early)");
        foreach (var (group, index) in groups.Select((g, i) => (g, i)))
            Log.Info($"  group {index + 1}: {string.Join(", ", group.Select(t => t.Name))}");

        var clock = Stopwatch.StartNew();
        var runners = groups
            .Select((group, index) => new GroupRunner(index + 1, group, RateFor(ratePerSecond, group.Count, tables.Count)))
            .ToList();

        using var reporting = new CancellationTokenSource();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        var loops = runners.Select(r => r.RunAsync(clock, duration, linked.Token)).ToList();
        var reporter = ReportAsync(runners, clock, reporting.Token);

        try
        {
            await Task.WhenAll(loops);
        }
        catch (OperationCanceledException)
        {
            Log.Info("stopping early");
        }
        finally
        {
            await reporting.CancelAsync();
            await reporter;
        }

        Log.Ok($"{Combined(runners)} over {Describe(clock.Elapsed)}");
        foreach (var runner in runners)
            Log.Info($"  group {runner.Number}: {runner.Describe()}");
    }

    /// <summary>
    /// Tables split into <paramref name="groups"/> as evenly as the count allows, the remainder
    /// distributed one per group from the first — 17 tables across 4 groups gives 5/4/4/4 rather than
    /// demanding that the count divide evenly.
    /// </summary>
    internal static List<List<HarnessTable>> Split(IReadOnlyList<HarnessTable> tables, int groups)
    {
        var result = new List<List<HarnessTable>>(groups);
        var baseSize = tables.Count / groups;
        var remainder = tables.Count % groups;

        var offset = 0;
        for (var i = 0; i < groups; i++)
        {
            var size = baseSize + (i < remainder ? 1 : 0);
            result.Add([.. tables.Skip(offset).Take(size)]);
            offset += size;
        }

        return result;
    }

    /// <summary>
    /// A group's share of the total rate, in proportion to how many tables it holds.
    /// <para>
    /// Proportional rather than equal per group, because an uneven split otherwise gives the smaller
    /// groups' tables a higher rate than the larger groups' — the goal is that every *table* sees
    /// about <c>R / N</c>, not that every group sees <c>R / P</c>.
    /// </para>
    /// </summary>
    internal static double RateFor(int totalRate, int groupTableCount, int totalTables) =>
        (double)totalRate * groupTableCount / totalTables;

    private sealed class GroupRunner(int number, IReadOnlyList<HarnessTable> tables, double ratePerSecond)
    {
        private readonly Dictionary<string, TableState> _states =
            tables.ToDictionary(t => t.Name, t => new TableState(t));

        public int Number => number;

        public async Task RunAsync(Stopwatch clock, TimeSpan duration, CancellationToken cancellationToken)
        {
            await using var connection = await SqlBootstrap.OpenAsync(
                Scenario.SourceConnectionString(Scenario.DatabaseName), cancellationToken);

            foreach (var state in _states.Values)
                await state.LoadAsync(connection, cancellationToken);

            var random = new Random(number * 7919);
            var interval = TimeSpan.FromSeconds(1.0 / ratePerSecond);
            var cursor = 0;

            while (clock.Elapsed < duration && !cancellationToken.IsCancellationRequested)
            {
                var tickStart = clock.Elapsed;

                // The rotation, not a random pick: every table in the group gets its turn on a fixed
                // schedule, so an idle table means something.
                var state = _states[tables[cursor].Name];
                cursor = (cursor + 1) % tables.Count;

                await state.ActAsync(connection, random, cancellationToken);

                var elapsed = clock.Elapsed - tickStart;
                if (elapsed < interval)
                    await Task.Delay(interval - elapsed, cancellationToken);
            }
        }

        public Counts Total()
        {
            var total = new Counts();
            foreach (var state in _states.Values)
                total.Add(state.Counts);
            return total;
        }

        public string Describe() =>
            $"{Total()} — " + string.Join(", ", tables.Select(t => $"{t.Name} {_states[t.Name].Counts.Total():N0}"));
    }

    /// <summary>
    /// One table's live keys and its own counters. Keys are held in memory for the run rather than
    /// re-queried each turn: picking an update or delete target is the hot path, and a round trip per
    /// transaction just to choose one would cap the achievable rate well below what the writer side
    /// can absorb. One list per table, so an update always targets a real row of the table whose turn
    /// it is.
    /// </summary>
    private sealed class TableState(HarnessTable table)
    {
        private readonly List<int> _liveIds = [];
        private int _nextId;

        public Counts Counts { get; } = new();

        public async Task LoadAsync(SqlConnection connection, CancellationToken cancellationToken)
        {
            _nextId = await SqlBootstrap.MaxIdAsync(connection, table, cancellationToken) + 1;

            await using var cmd = connection.CreateCommand();
            cmd.CommandText = $"SELECT Id FROM {table.QualifiedSource};";
            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                _liveIds.Add(reader.GetInt32(0));

            if (_liveIds.Count == 0)
                Log.Warn($"{table.Name} is empty — its first transactions will all be inserts. `seed` first for a fuller mix.");
        }

        /// <summary>
        /// Inserts stay in the majority so the tables grow over a long run, but updates and deletes are
        /// frequent enough that the reader has all three operations to carry and a merge writer has
        /// something to do beyond appending. Unchanged from the single-table workload — only *which
        /// table* a turn lands on became deterministic.
        /// </summary>
        public async Task ActAsync(SqlConnection connection, Random random, CancellationToken cancellationToken)
        {
            var roll = random.NextDouble();
            if (_liveIds.Count == 0 || roll < 0.5)
            {
                await InsertAsync(connection, _nextId, random, cancellationToken);
                _liveIds.Add(_nextId++);
                Counts.Inserted++;
            }
            else if (roll < 0.85)
            {
                await UpdateAsync(connection, _liveIds[random.Next(_liveIds.Count)], random, cancellationToken);
                Counts.Updated++;
            }
            else
            {
                var index = random.Next(_liveIds.Count);
                await DeleteAsync(connection, _liveIds[index], cancellationToken);
                // Swap-remove: order doesn't matter here and it keeps this O(1) at large sizes.
                _liveIds[index] = _liveIds[^1];
                _liveIds.RemoveAt(_liveIds.Count - 1);
                Counts.Deleted++;
            }
        }

        private async Task InsertAsync(SqlConnection connection, int id, Random random, CancellationToken cancellationToken)
        {
            var columns = table.Columns;
            await using var cmd = connection.CreateCommand();
            cmd.CommandText =
                $"INSERT INTO {table.QualifiedSource} ({string.Join(", ", columns.Select(c => $"[{c.Name}]"))}) " +
                $"VALUES ({string.Join(", ", columns.Select((_, i) => $"@p{i}"))});";
            for (var i = 0; i < columns.Count; i++)
                cmd.Parameters.AddWithValue($"@p{i}", Scenario.Value(columns[i], id, random));
            await cmd.ExecuteNonQueryAsync(cancellationToken);
        }

        /// <summary>Every non-key column is rewritten, so an update is a real change on a wide table
        /// rather than one column moving while the other forty sit still.</summary>
        private async Task UpdateAsync(SqlConnection connection, int id, Random random, CancellationToken cancellationToken)
        {
            var assignable = table.Columns.Where(c => c.Type != HarnessColumnType.Key).ToList();
            await using var cmd = connection.CreateCommand();
            cmd.CommandText =
                $"UPDATE {table.QualifiedSource} SET " +
                string.Join(", ", assignable.Select((c, i) => $"[{c.Name}] = @p{i}")) +
                " WHERE Id = @id;";
            for (var i = 0; i < assignable.Count; i++)
                cmd.Parameters.AddWithValue($"@p{i}", Scenario.Value(assignable[i], id, random));
            cmd.Parameters.AddWithValue("@id", id);
            await cmd.ExecuteNonQueryAsync(cancellationToken);
        }

        private async Task DeleteAsync(SqlConnection connection, int id, CancellationToken cancellationToken)
        {
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = $"DELETE FROM {table.QualifiedSource} WHERE Id = @id;";
            cmd.Parameters.AddWithValue("@id", id);
            await cmd.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    /// <summary>
    /// The periodic line, reporting combined and per-group figures. A group falling behind, or one
    /// table going quiet, is the thing worth seeing here — an aggregate number hides exactly that.
    /// </summary>
    private static async Task ReportAsync(
        IReadOnlyList<GroupRunner> runners, Stopwatch clock, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
                Log.Info($"{Combined(runners)} @ {Describe(clock.Elapsed)}");
                foreach (var runner in runners)
                    Log.Info($"    group {runner.Number}: {runner.Describe()}");
            }
        }
        catch (OperationCanceledException)
        {
            // The run finished; the final summary is printed by the caller.
        }
    }

    private static Counts Combined(IReadOnlyList<GroupRunner> runners)
    {
        var total = new Counts();
        foreach (var runner in runners)
            total.Add(runner.Total());
        return total;
    }

    internal sealed class Counts
    {
        public int Inserted;
        public int Updated;
        public int Deleted;

        public int Total() => Inserted + Updated + Deleted;

        public void Add(Counts other)
        {
            Inserted += other.Inserted;
            Updated += other.Updated;
            Deleted += other.Deleted;
        }

        public override string ToString() => $"{Inserted:N0} inserted, {Updated:N0} updated, {Deleted:N0} deleted";
    }

    public static string Describe(TimeSpan duration) =>
        duration.TotalMinutes >= 1 ? $"{duration.TotalMinutes:0.#}m" : $"{duration.TotalSeconds:0.#}s";
}
