using System.Globalization;
using Microsoft.Data.SqlClient;

namespace DataSync.DevHarness;

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

    public static async Task<bool> VerifyAsync(CancellationToken cancellationToken)
    {
        Log.Step("Comparing source and target");

        await using var source = await SqlBootstrap.OpenAsync(Scenario.SourceConnectionString(Scenario.DatabaseName), cancellationToken);
        await using var target = await SqlBootstrap.OpenAsync(Scenario.TargetConnectionString(Scenario.DatabaseName), cancellationToken);

        await using var sourceReader = await OpenOrderedReaderAsync(source, cancellationToken);
        await using var targetReader = await OpenOrderedReaderAsync(target, cancellationToken);

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
                if (Compare(sourceReader, targetReader) is string difference)
                    different.Add(difference);
                else
                    matched++;

                hasSource = await sourceReader.ReadAsync(cancellationToken);
                hasTarget = await targetReader.ReadAsync(cancellationToken);
            }
        }

        return Report(matched, missing, extra, different);
    }

    private static async Task<SqlDataReader> OpenOrderedReaderAsync(SqlConnection connection, CancellationToken cancellationToken)
    {
        var cmd = connection.CreateCommand();
        cmd.CommandText =
            $"SELECT {string.Join(", ", Scenario.Columns.Select(c => $"[{c}]"))} FROM {Scenario.QualifiedTable} ORDER BY Id;";
        return await cmd.ExecuteReaderAsync(cancellationToken);
    }

    /// <summary>Returns a description of the first column that disagrees, or null if the rows match.</summary>
    private static string? Compare(SqlDataReader source, SqlDataReader target)
    {
        for (var i = 1; i < Scenario.Columns.Length; i++)
        {
            var sourceValue = source.IsDBNull(i) ? null : source.GetValue(i);
            var targetValue = target.IsDBNull(i) ? null : target.GetValue(i);

            if (!Equals(sourceValue, targetValue))
                return $"Id {source.GetInt32(0)}: {Scenario.Columns[i]} is " +
                       $"{Format(targetValue)} at the target, {Format(sourceValue)} at the source";
        }

        return null;
    }

    private static string Format(object? value) => value switch
    {
        null => "NULL",
        DateTime dt => dt.ToString("O", CultureInfo.InvariantCulture),
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => $"'{value}'",
    };

    private static bool Report(int matched, List<int> missing, List<int> extra, List<string> different)
    {
        var total = missing.Count + extra.Count + different.Count;
        if (total == 0)
        {
            Log.Ok($"source and target match — {matched:N0} row(s) identical");
            return true;
        }

        Log.Error($"{total:N0} difference(s) across {matched + different.Count:N0} compared row(s)");

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
