using DbDataSync.Drivers.Generic;
using Parquet;
using Parquet.Schema;

namespace DbDataSync.Verification;

/// <summary>
/// A check's result on disk, as parquet.
/// <para>
/// Parquet rather than rows in the state store, because a result is a **standalone artifact**: it can
/// be large, it is never updated, and nothing reads it transactionally. Putting it in SQLite would put
/// an arbitrarily large blob behind the single writer phase 39 exists to keep responsive, for no
/// benefit — the state store keeps only the index saying where this file is.
/// </para>
/// <para>
/// One row per compared group. Grouping values are strings because that is what they were compared
/// as: two engines that render a value differently were never comparable, and storing the rendered
/// text stores what the comparison actually saw.
/// </para>
/// </summary>
public static class VerificationResultFile
{
    private const string StatusColumn = "__status";
    private const string SourceSuffix = "__source";
    private const string TargetSuffix = "__target";
    private const string DifferenceSuffix = "__difference";

    // Everything the columns cannot carry, in the file's own metadata — so a result is readable from
    // the file alone rather than only in the company of the config that produced it.
    private const string CheckNameKey = "checkName";
    private const string ThresholdKey = "differenceThreshold";
    private const string SourceReadAtKey = "sourceReadAtUtc";
    private const string TargetReadAtKey = "targetReadAtUtc";
    private const string GroupColumnsKey = "groupColumns";
    private const string MeasureColumnsKey = "measureColumns";

    public static async Task WriteAsync(string path, VerificationResult result, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);

        var groupFields = result.GroupColumns.Select(c => new DataField<string>(c)).ToList();
        var measureFields = result.MeasureColumns
            .SelectMany(m => new[]
            {
                // Nullable throughout: a group present on one side only has no value for the other,
                // and a zero there would be a number nobody measured.
                new DataField<double?>(m + SourceSuffix),
                new DataField<double?>(m + TargetSuffix),
                new DataField<double?>(m + DifferenceSuffix),
            })
            .ToList();
        var statusField = new DataField<string>(StatusColumn);

        var schema = new ParquetSchema([.. groupFields, .. measureFields, statusField]);

        await using var stream = File.Create(path);
        await using var writer = await ParquetWriter.CreateAsync(schema, stream, cancellationToken: cancellationToken);
        writer.CustomMetadata = new Dictionary<string, string>
        {
            [CheckNameKey] = result.CheckName,
            [ThresholdKey] = result.DifferenceThreshold.ToString("R"),
            [SourceReadAtKey] = result.SourceReadAtUtc.ToString("O"),
            [TargetReadAtKey] = result.TargetReadAtUtc.ToString("O"),
            [GroupColumnsKey] = string.Join('\n', result.GroupColumns),
            [MeasureColumnsKey] = string.Join('\n', result.MeasureColumns),
        };

        // A result is written once and read whole; one row group keeps both ends simple.
        using var rowGroup = writer.CreateRowGroup();

        for (var i = 0; i < groupFields.Count; i++)
        {
            var ordinal = i;
            // The string overload, not the generic one: it takes a collection rather than a memory of
            // nullables, because a string is already a reference type.
            await rowGroup.WriteAsync(
                groupFields[ordinal], result.Rows.Select(r => Value(r.Group, ordinal)).ToArray(), null);
        }

        var field = 0;
        foreach (var measure in result.MeasureColumns)
        {
            await WriteMeasureAsync(measureFields[field++], result.Rows.Select(r => Get(r.Source, measure)));
            await WriteMeasureAsync(measureFields[field++], result.Rows.Select(r => Get(r.Target, measure)));
            await WriteMeasureAsync(measureFields[field++], result.Rows.Select(r => Get(r.Differences, measure)));
        }

        await rowGroup.WriteAsync(statusField, result.Rows.Select(r => r.Status.ToString()).ToArray(), null);

        Task WriteMeasureAsync(DataField target, IEnumerable<double?> values) =>
            rowGroup.WriteAsync<double>(target, values.ToArray().AsMemory(), null, null, cancellationToken);
    }

    /// <summary>
    /// Everything about a result except its rows: the parquet footer, and nothing else.
    /// <para>
    /// There is deliberately no "read the whole thing" here any more. A check over a large table
    /// produces a row per group, and a method whose only correct use is "when you know the file is
    /// small" is a loaded gun — it was the one that locked the UI up. Rows come a page at a time,
    /// through <see cref="VerificationResultQuery"/>.
    /// </para>
    /// </summary>
    public static async Task<VerificationResultHeader> ReadHeaderAsync(string path, CancellationToken cancellationToken)
    {
        await using var reader = await ParquetReader.CreateAsync(path, cancellationToken: cancellationToken);
        var metadata = reader.CustomMetadata;

        return new VerificationResultHeader(
            metadata.GetValueOrDefault(CheckNameKey) ?? "",
            Split(metadata.GetValueOrDefault(GroupColumnsKey)),
            Split(metadata.GetValueOrDefault(MeasureColumnsKey)),
            VerificationResultQuery.ParseThreshold(metadata.GetValueOrDefault(ThresholdKey)),
            ParseTime(metadata.GetValueOrDefault(SourceReadAtKey)),
            ParseTime(metadata.GetValueOrDefault(TargetReadAtKey)));
    }

    private static string Value(IReadOnlyList<string> group, int ordinal) =>
        ordinal < group.Count ? group[ordinal] : "";

    private static double? Get(IReadOnlyDictionary<string, double>? values, string measure) =>
        values is not null && values.TryGetValue(measure, out var value) ? value : null;

    private static IReadOnlyList<string> Split(string? value) =>
        string.IsNullOrEmpty(value) ? [] : value.Split('\n');

    private static DateTimeOffset ParseTime(string? value) =>
        DateTimeOffset.TryParse(value, null, System.Globalization.DateTimeStyles.RoundtripKind, out var parsed)
            ? parsed
            : default;
}
