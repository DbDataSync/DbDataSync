using System.Globalization;
using System.Data.Common;

namespace DbDataSync.DevHarness;

/// <summary>
/// Compares source and target row by row and reports exactly what differs.
/// <para>
/// A merge-join over two key-ordered streams rather than pulling either side into memory or hashing
/// the whole table: it holds one row from each side at a time regardless of size, and — unlike an
/// aggregate checksum, which can only ever say "these differ" — it names the rows, which is the
/// difference between a signal and a diagnosis.
/// </para>
/// </summary>
public static class Verifier
{
    private const int MaxReportedDifferences = 20;

    public static async Task<bool> VerifyAsync(
        TargetEngine engine, IReadOnlyList<HarnessTable> tables, CancellationToken cancellationToken)
    {
        Log.Step($"Comparing the source with the {engine.Name} target across {tables.Count} table(s)");

        await using var source = await SqlBootstrap.OpenAsync(Scenario.SourceConnectionString(Scenario.DatabaseName), cancellationToken);
        await using var target = await engine.OpenAsync(Scenario.DatabaseName, cancellationToken);

        // Every table is compared even once one has failed: "which tables diverged" is the useful
        // answer, and stopping at the first would report the narrowest one and hide the rest.
        var allMatch = true;
        foreach (var table in tables)
            allMatch &= await VerifyTableAsync(engine, source, target, table, cancellationToken);

        if (allMatch)
            Log.Ok($"all {tables.Count} table(s) match");

        return allMatch;
    }

    private static async Task<bool> VerifyTableAsync(
        TargetEngine engine, DbConnection source, DbConnection target, HarnessTable table,
        CancellationToken cancellationToken)
    {
        var columns = table.ColumnNames.ToList();

        await using var sourceReader = await OpenOrderedReaderAsync(
            source, table.QualifiedSource, columns, TargetEngine.MsSql.Quote, cancellationToken);
        await using var targetReader = await OpenOrderedReaderAsync(
            target, engine.QualifiedTable(table), columns, engine.Quote, cancellationToken);

        var missing = new List<int>();      // in the source, absent from the target
        var extra = new List<int>();        // in the target, absent from the source
        var different = new List<string>(); // present on both, values disagree
        var matched = 0;

        var hasSource = await sourceReader.ReadAsync(cancellationToken);
        var hasTarget = await targetReader.ReadAsync(cancellationToken);

        while (hasSource || hasTarget)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!hasTarget || (hasSource && sourceReader.GetInt32(0) < targetReader.GetInt32(0)))
            {
                missing.Add(sourceReader.GetInt32(0));
                hasSource = await sourceReader.ReadAsync(cancellationToken);
            }
            else if (!hasSource || targetReader.GetInt32(0) < sourceReader.GetInt32(0))
            {
                extra.Add(targetReader.GetInt32(0));
                hasTarget = await targetReader.ReadAsync(cancellationToken);
            }
            else
            {
                if (Compare(sourceReader, targetReader, columns) is string difference)
                    different.Add(difference);
                else
                    matched++;

                hasSource = await sourceReader.ReadAsync(cancellationToken);
                hasTarget = await targetReader.ReadAsync(cancellationToken);
            }
        }

        return Report(table, matched, missing, extra, different);
    }

    /// <summary>Each side quotes and qualifies in its own dialect: the merge-join itself is the same
    /// on either engine, which is what makes cross-engine verification the same code path.</summary>
    private static async Task<DbDataReader> OpenOrderedReaderAsync(
        DbConnection connection, string qualifiedTable, IReadOnlyList<string> columns,
        Func<string, string> quote, CancellationToken cancellationToken)
    {
        var cmd = connection.CreateCommand();
        cmd.CommandText =
            $"SELECT {string.Join(", ", columns.Select(quote))} FROM {qualifiedTable} ORDER BY {quote("Id")};";
        return await cmd.ExecuteReaderAsync(cancellationToken);
    }

    /// <summary>Returns a description of the first column that disagrees, or null if the rows match.</summary>
    private static string? Compare(DbDataReader source, DbDataReader target, IReadOnlyList<string> columns)
    {
        for (var i = 1; i < columns.Count; i++)
        {
            var sourceValue = source.IsDBNull(i) ? null : source.GetValue(i);
            var targetValue = target.IsDBNull(i) ? null : target.GetValue(i);

            if (!ValuesMatch(sourceValue, targetValue))
                return $"Id {source.GetInt32(0)}: {columns[i]} is " +
                       $"{Format(targetValue)} at the target, {Format(sourceValue)} at the source";
        }

        return null;
    }

    /// <summary>
    /// Cross-engine comparison cannot use <see cref="object.Equals(object?)"/> alone: the same stored
    /// value comes back as a different CLR type from each provider — a SQL Server <c>datetime2</c>
    /// arrives as <see cref="DateTime"/> and a Postgres <c>timestamp</c> may arrive with a different
    /// <see cref="DateTimeKind"/>, and a <c>decimal</c>'s trailing zeros differ by declared scale.
    /// Comparing the *values* rather than the boxes is the only thing that means what it says here.
    /// </summary>
    private static bool ValuesMatch(object? source, object? target)
    {
        if (source is null || target is null)
            return source is null && target is null;

        if (source is DateTime a && target is DateTime b)
            return a.Ticks == b.Ticks;

        if (source is decimal x && target is decimal y)
            return x == y;

        if (source is IConvertible && target is IConvertible && source.GetType() != target.GetType())
            return Format(source) == Format(target);

        return Equals(source, target);
    }

    private static string Format(object? value) => value switch
    {
        null => "NULL",
        DateTime dt => dt.ToString("O", CultureInfo.InvariantCulture),
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => $"'{value}'",
    };

    private static bool Report(
        HarnessTable table, int matched, List<int> missing, List<int> extra, List<string> different)
    {
        var total = missing.Count + extra.Count + different.Count;
        if (total == 0)
        {
            Log.Ok($"{table.Name}: source and target match — {matched:N0} row(s) identical");
            return true;
        }

        Log.Error($"{table.Name}: {total:N0} difference(s) across {matched + different.Count:N0} compared row(s)");

        if (missing.Count > 0)
            Log.Info($"{missing.Count:N0} row(s) in the source but not the target: {Sample(missing)}");
        if (extra.Count > 0)
            Log.Info($"{extra.Count:N0} row(s) in the target but not the source: {Sample(extra)}");

        foreach (var difference in different.Take(MaxReportedDifferences))
            Log.Info(difference);
        if (different.Count > MaxReportedDifferences)
            Log.Info($"…and {different.Count - MaxReportedDifferences:N0} more differing row(s)");

        return false;
    }

    private static string Sample(List<int> ids)
    {
        var shown = string.Join(", ", ids.Take(10));
        return ids.Count > 10 ? $"{shown}, …" : shown;
    }
}
