using System.Globalization;

namespace DbDataSync.Drivers.Generic;

/// <summary>Which side (or sides) a compared row was found on.</summary>
public enum VerificationRowStatus
{
    /// <summary>On both, and every measure within the threshold.</summary>
    Match,

    /// <summary>On both, and at least one measure outside it.</summary>
    Differs,

    /// <summary>On the source only.</summary>
    MissingFromTarget,

    /// <summary>On the target only — a row the source no longer has, or never had.</summary>
    MissingFromSource,
}

/// <param name="Group">The grouping values, in the check's declared order. Empty for an ungrouped check.</param>
/// <param name="Source">Measure values from the source, by result column name. Null when absent there.</param>
/// <param name="Differences">
/// Target minus source, per measure. Present for a row on both sides even when within the threshold —
/// the number is what an operator reads to judge drift, and hiding the small ones would leave them
/// unable to tell "equal" from "close".
/// </param>
public sealed record VerificationRow(
    IReadOnlyList<string> Group,
    IReadOnlyDictionary<string, double>? Source,
    IReadOnlyDictionary<string, double>? Target,
    IReadOnlyDictionary<string, double> Differences,
    VerificationRowStatus Status);

/// <summary>One side's result set, as it came back.</summary>
/// <param name="ExecutedAtUtc">
/// When this side's query ran. Captured per side and shown, because the gap between the two reads is
/// what tells an operator whether a difference is drift or a defect — and guessing at it is what they
/// would otherwise have to do.
/// </param>
public sealed record VerificationSide(
    IReadOnlyList<IReadOnlyList<string>> Groups,
    IReadOnlyList<IReadOnlyDictionary<string, double>> Measures,
    DateTimeOffset ExecutedAtUtc);

/// <summary>
/// Lines up two result sets and says where they disagree.
/// <para>
/// A merge join over two key-ordered streams, the same shape the dev harness's verifier uses: it holds
/// one row from each side rather than either whole set, and — unlike a checksum, which can only say
/// "these differ" — it names the groups, which is the difference between a signal and a diagnosis.
/// </para>
/// </summary>
public static class VerificationComparison
{
    private static readonly IReadOnlyDictionary<string, double> NoDifferences = new Dictionary<string, double>();

    /// <summary>
    /// <paramref name="threshold"/> is a fraction of the larger side, so it means the same thing for a
    /// group of ten rows and a group of ten million. Zero means any difference at all counts.
    /// <para>
    /// A threshold rather than "anything nonzero" because a replication is behind by design: a check
    /// run mid-pass will differ, and a screen that paints that red teaches an operator to ignore red.
    /// </para>
    /// </summary>
    public static IReadOnlyList<VerificationRow> Compare(
        VerificationSide source, VerificationSide target, double threshold)
    {
        var rows = new List<VerificationRow>();
        int s = 0, t = 0;

        while (s < source.Groups.Count || t < target.Groups.Count)
        {
            var order = s >= source.Groups.Count ? 1
                : t >= target.Groups.Count ? -1
                : CompareGroups(source.Groups[s], target.Groups[t]);

            if (order < 0)
            {
                rows.Add(new VerificationRow(
                    source.Groups[s], source.Measures[s], null, NoDifferences, VerificationRowStatus.MissingFromTarget));
                s++;
            }
            else if (order > 0)
            {
                rows.Add(new VerificationRow(
                    target.Groups[t], null, target.Measures[t], NoDifferences, VerificationRowStatus.MissingFromSource));
                t++;
            }
            else
            {
                rows.Add(Compare(source.Groups[s], source.Measures[s], target.Measures[t], threshold));
                s++;
                t++;
            }
        }

        return rows;
    }

    private static VerificationRow Compare(
        IReadOnlyList<string> group,
        IReadOnlyDictionary<string, double> source,
        IReadOnlyDictionary<string, double> target,
        double threshold)
    {
        var differences = new Dictionary<string, double>();
        var differs = false;

        foreach (var measure in source.Keys.Concat(target.Keys).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            source.TryGetValue(measure, out var sourceValue);
            target.TryGetValue(measure, out var targetValue);

            var difference = targetValue - sourceValue;
            differences[measure] = difference;

            if (Exceeds(difference, sourceValue, targetValue, threshold))
                differs = true;
        }

        return new VerificationRow(
            group, source, target, differences,
            differs ? VerificationRowStatus.Differs : VerificationRowStatus.Match);
    }

    /// <summary>
    /// Relative to the larger side, so a threshold means the same thing at any scale. Two zeroes are
    /// equal rather than a division by zero, and a measure that went from zero to anything is always
    /// over the threshold — there is no proportion of nothing.
    /// </summary>
    private static bool Exceeds(double difference, double source, double target, double threshold)
    {
        if (difference == 0)
            return false;
        if (threshold <= 0)
            return true;

        var scale = Math.Max(Math.Abs(source), Math.Abs(target));
        return scale == 0 || Math.Abs(difference) / scale > threshold;
    }

    /// <summary>
    /// Ordinal, and it has to match the <c>ORDER BY</c> the statements carry — the merge join is only
    /// correct if both sides arrive in the order this compares them in. String comparison because a
    /// grouping value arrives as whatever the engine rendered it as, and the two engines have to agree
    /// on the text or they were never comparable in the first place.
    /// </summary>
    private static int CompareGroups(IReadOnlyList<string> left, IReadOnlyList<string> right)
    {
        for (var i = 0; i < Math.Min(left.Count, right.Count); i++)
        {
            var order = string.CompareOrdinal(left[i], right[i]);
            if (order != 0)
                return order;
        }
        return left.Count.CompareTo(right.Count);
    }

    /// <summary>A grouping value as text, for comparison and for display. Invariant, so the same value
    /// read through two providers compares equal rather than depending on the host's locale.</summary>
    public static string GroupValue(object? value) => value switch
    {
        null => "",
        DateTime dt => dt.ToString("O", CultureInfo.InvariantCulture),
        DateTimeOffset dto => dto.UtcDateTime.ToString("O", CultureInfo.InvariantCulture),
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? "",
    };
}
