using System.Data;
using System.Globalization;
using System.Text;
using DataSync.Drivers.Generic;
using DuckDB.NET.Data;
using Parquet;

namespace DataSync.Verification;

/// <summary>Everything about a result except its rows — cheap to read, because it is the parquet
/// footer and nothing else.</summary>
public sealed record VerificationResultHeader(
    string CheckName,
    IReadOnlyList<string> GroupColumns,
    IReadOnlyList<string> MeasureColumns,
    double DifferenceThreshold,
    DateTimeOffset SourceReadAtUtc,
    DateTimeOffset TargetReadAtUtc);

/// <param name="TotalRows">Every compared group in the file, whatever the current filter is.</param>
/// <param name="DifferingRows">Groups the two sides disagreed on, or that only one side had.</param>
public sealed record VerificationResultPage(
    string CheckName,
    IReadOnlyList<string> GroupColumns,
    IReadOnlyList<string> MeasureColumns,
    double DifferenceThreshold,
    DateTimeOffset SourceReadAtUtc,
    DateTimeOffset TargetReadAtUtc,
    long TotalRows,
    long DifferingRows,
    int Offset,
    IReadOnlyList<VerificationRow> Rows)
{
    /// <summary>How far apart the two reads were. A difference is only as meaningful as this is
    /// small, which is why every page carries it rather than the first one alone.</summary>
    public TimeSpan ReadGap => (TargetReadAtUtc - SourceReadAtUtc).Duration();
}

/// <summary>
/// Reads a page of a check's result, by querying the parquet with SQL.
/// <para>
/// This replaced reading the whole file. A check over a large table produces a row per group — which
/// can be millions — and the old path deserialised every one of them into memory here, serialised all
/// of them into one JSON response, and handed the browser a DOM node per cell. It locked the UI up for
/// minutes and crashed tabs, and it did so on the machine as well: the API allocated the entire result
/// to answer a request that only ever showed the first screenful.
/// </para>
/// <para>
/// DuckDB rather than paging Parquet.Net's row groups by hand. Offset, filter and count over parquet
/// is exactly what it is for — it pushes the predicate down and reads only the column chunks the
/// query touches — and hand-rolling that would be a lot of engineering to arrive somewhere slower.
/// The file is read where it lies; nothing is imported and no database file is created.
/// </para>
/// </summary>
public static class VerificationResultQuery
{
    internal const string StatusColumn = "__status";
    internal const string SourceSuffix = "__source";
    internal const string TargetSuffix = "__target";
    internal const string DifferenceSuffix = "__difference";

    /// <summary>The most rows one request will return, however many were asked for. A page is a
    /// screenful somebody reads, and a client asking for a million of them is asking for the bug this
    /// replaced.</summary>
    public const int MaxPageSize = 500;

    public static async Task<VerificationResultPage> ReadPageAsync(
        string path, int offset, int limit, bool differingOnly, CancellationToken cancellationToken)
    {
        var header = await VerificationResultFile.ReadHeaderAsync(path, cancellationToken);

        offset = Math.Max(0, offset);
        limit = Math.Clamp(limit, 1, MaxPageSize);

        await using var connection = new DuckDBConnection("DataSource=:memory:");
        await connection.OpenAsync(cancellationToken);

        var (total, differing) = await CountAsync(connection, path, cancellationToken);
        var rows = await RowsAsync(connection, path, header, offset, limit, differingOnly, cancellationToken);

        return new VerificationResultPage(
            header.CheckName, header.GroupColumns, header.MeasureColumns, header.DifferenceThreshold,
            header.SourceReadAtUtc, header.TargetReadAtUtc, total, differing, offset, rows);
    }

    /// <summary>
    /// Both counts in one pass. They are what the pager and the summary line are built from, and they
    /// come from the parquet's own statistics rather than from reading the rows.
    /// </summary>
    private static async Task<(long Total, long Differing)> CountAsync(
        DuckDBConnection connection, string path, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            $"SELECT count(*), count(*) FILTER (WHERE {Quote(StatusColumn)} <> 'Match') FROM read_parquet($path);";
        Bind(command, "path", path);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            return (0, 0);

        return (reader.GetInt64(0), reader.GetInt64(1));
    }

    private static async Task<IReadOnlyList<VerificationRow>> RowsAsync(
        DuckDBConnection connection, string path, VerificationResultHeader header,
        int offset, int limit, bool differingOnly, CancellationToken cancellationToken)
    {
        var selected = new List<string>();
        selected.AddRange(header.GroupColumns.Select(Quote));
        foreach (var measure in header.MeasureColumns)
        {
            selected.Add(Quote(measure + SourceSuffix));
            selected.Add(Quote(measure + TargetSuffix));
            selected.Add(Quote(measure + DifferenceSuffix));
        }
        selected.Add(Quote(StatusColumn));

        var sql = new StringBuilder()
            .Append("SELECT ").Append(string.Join(", ", selected))
            .Append(" FROM read_parquet($path)");
        if (differingOnly)
            sql.Append($" WHERE {Quote(StatusColumn)} <> 'Match'");
        // Ordered by the file's own row order, so paging is stable and page two follows page one.
        // Parquet has no key to sort by that would be cheaper or more meaningful.
        sql.Append(" LIMIT $limit OFFSET $offset;");

        await using var command = connection.CreateCommand();
        command.CommandText = sql.ToString();
        Bind(command, "path", path);
        Bind(command, "limit", limit);
        Bind(command, "offset", offset);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var rows = new List<VerificationRow>();

        while (await reader.ReadAsync(cancellationToken))
        {
            var ordinal = 0;
            var group = header.GroupColumns.Select(_ => Text(reader, ordinal++)).ToList();

            var source = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            var target = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            var differences = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            foreach (var measure in header.MeasureColumns)
            {
                Add(source, measure, Number(reader, ordinal++));
                Add(target, measure, Number(reader, ordinal++));
                Add(differences, measure, Number(reader, ordinal++));
            }

            var status = Enum.Parse<VerificationRowStatus>(Text(reader, ordinal));

            // Null rather than an empty dictionary for the side a group was absent from — the two mean
            // different things, and a zero there would be a number nobody measured.
            rows.Add(new VerificationRow(
                group,
                status == VerificationRowStatus.MissingFromSource ? null : source,
                status == VerificationRowStatus.MissingFromTarget ? null : target,
                differences,
                status));
        }

        return rows;
    }

    private static void Add(Dictionary<string, double> values, string measure, double? value)
    {
        if (value is { } present)
            values[measure] = present;
    }

    private static string Text(IDataRecord reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? "" : reader.GetString(ordinal);

    private static double? Number(IDataRecord reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetDouble(ordinal);

    /// <summary>
    /// A column name comes from the operator's own mapping, so it is quoted rather than interpolated
    /// bare — a target column called <c>order"count</c> is legal and would otherwise end the
    /// identifier and leave the rest as SQL.
    /// </summary>
    private static string Quote(string identifier) => $"\"{identifier.Replace("\"", "\"\"")}\"";

    private static void Bind(DuckDBCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }

    internal static double ParseThreshold(string? value) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) ? parsed : 0;
}
