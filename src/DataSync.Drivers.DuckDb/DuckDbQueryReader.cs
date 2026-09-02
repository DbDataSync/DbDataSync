using System.Data.Common;
using System.Runtime.CompilerServices;
using DataSync.Core.Config;
using DataSync.Core.Sql;
using DataSync.Drivers.Abstractions;
using DataSync.Drivers.Generic;

namespace DataSync.Drivers.DuckDb;

/// <summary>
/// Runs a query an operator wrote, as a full reload.
/// <para>
/// **The query is the configuration.** Every other reader here is handed a table and composes a
/// statement for it; this one is handed a statement and composes nothing. That is what makes DuckDB
/// worth a driver at all — the interesting sources are not its tables, they are what its scanners
/// reach from inside a query (<c>read_parquet</c>, <c>read_csv</c>, an attached Postgres, an S3 glob),
/// none of which a table picker can name.
/// </para>
/// <para>
/// **Deliberately not incremental**, and the same shape <see cref="BatchReloadReader"/> established:
/// <c>previousWatermark</c> is ignored outright and echoed back unchanged, so a standalone reload
/// replication — whose Primary passes do persist whatever comes back here — leaves the stored
/// watermark exactly as it found it rather than writing a meaningless one over it. There is no
/// watermark token, no reserved result column and no half-built hook for one: a query-first
/// incremental source needs design work this phase deliberately deferred, and a partial mechanism
/// would be the thing the next phase has to undo first.
/// </para>
/// <para>
/// Segmenting works, and works without a predicate: see <see cref="QuerySegmentTokens"/>. There is no
/// <see cref="ISegmentExpandingReader"/> — auto-segment discovery would mean asking a query about its
/// own value range, which is a second execution of an arbitrary statement, and <c>AutoSegment</c>
/// never reaches a runtime reader in any case.
/// </para>
/// </summary>
public sealed class DuckDbQueryReader : IChangeReader, IStatementPreview
{
    public const string ReaderKind = "DuckDbQuery";

    /// <summary>The option holding the SQL. One setting, because there is only one decision.</summary>
    public const string QueryOption = "query";

    public string Kind => ReaderKind;

    /// <summary>
    /// A scan observes what exists and cannot observe what was removed — <see cref="BatchReloadReader"/>'s
    /// reasoning, and true here for the stronger reason that a query returning rows is the only thing
    /// this reader is given. Deletion is the reconciling writer's job on a reload.
    /// </summary>
    public bool DetectsDeletes => false;

    /// <summary>
    /// Declared with <see cref="ParameterType.Sql"/>, which is what puts a real editor on the
    /// mapping's source tab instead of a one-line box — the reason that member exists. The tokens are
    /// documented in the description rather than in a wiki, because the box is where somebody needs to
    /// know about them.
    /// </summary>
    public IReadOnlyList<ParameterDescriptor> Parameters { get; } =
    [
        new()
        {
            Name = QueryOption,
            Label = "Query",
            Description =
                "The SQL this reader runs, in DuckDB's dialect. Every row it returns is an insert. " +
                "To segment it, reference the segment's bounds in your own WHERE clause: " +
                "{{segmentColumn}} with {{segmentMin}}/{{segmentMax}} for a range, or with " +
                "{{segmentValues}} for a list. Left out, the query runs whole for every segment.",
            Type = ParameterType.Sql,
            Required = true,
        },
    ];

    public Task<ReadResult> ReadChangesAsync(
        DbConnection sourceConnection,
        SourceTableRef source,
        string? previousWatermark,
        IReadOnlyList<ColumnMapping> columnMappings,
        IReadOnlyDictionary<string, string> options,
        CancellationToken cancellationToken)
    {
        var query = QuerySegmentTokens.Substitute(QueryOf(options), SegmentSerializer.ReadOptional(options));

        // No projection of columnMappings into a SELECT list, unlike every other reader. The operator
        // chose the columns when they wrote the statement, and rewriting their SELECT list around a
        // mapping would mean parsing SQL we did not generate. Staging maps by name from the result set
        // this returns, which is the same contract ScriptedQueryReader's result already has.
        return Task.FromResult(new ReadResult(
            ReadRowsAsync(sourceConnection, query, cancellationToken),
            previousWatermark ?? ""));
    }

    /// <summary>
    /// The query, with whatever segment the preview's context carries already substituted — so a
    /// preview taken for one segment shows the statement that segment would actually run. An
    /// unsegmented preview shows the tokens as written, which is honest: that is the text, and where
    /// the bounds land in it is the operator's own arrangement rather than something to illustrate
    /// with invented values.
    /// </summary>
    public Task<IReadOnlyList<PreviewStatement>> DescribeAsync(
        PreviewRequest request, CancellationToken cancellationToken)
    {
        var segment = SegmentSerializer.ReadOptional(request.Options);
        var query = QuerySegmentTokens.Substitute(QueryOf(request.Options), segment);

        return Task.FromResult<IReadOnlyList<PreviewStatement>>(
        [
            new PreviewStatement(
                PreviewStages.SourceRead,
                segment is null ? "Run the query" : $"Run the query for the segment {segment.Describe()}",
                query,
                // The operator's, not ours — the whole statement, not a filter spliced into one.
                PreviewOrigin.OperatorSql,
                segment is null
                    ? "Unsegmented, so any {{segment…}} tokens are shown as written. A backfill supplies a " +
                      "segment, and substitutes them before running this."
                    : null),
        ]);
    }

    private static string QueryOf(IReadOnlyDictionary<string, string> options) =>
        options.TryGetValue(QueryOption, out var query) && !string.IsNullOrWhiteSpace(query)
            ? query
            : throw new InvalidOperationException(
                $"The '{ReaderKind}' reader needs a '{QueryOption}' option holding the SQL to run.");

    private static async IAsyncEnumerable<ChangeRow> ReadRowsAsync(
        DbConnection connection,
        string query,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var cmd = connection.CreateTimedCommand();
        cmd.CommandText = query;

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        var schema = ResultSetSchema.From(reader);
        while (await reader.ReadAsync(cancellationToken))
            yield return new ChangeRow(ChangeOperation.Insert, schema, ResultSetSchema.ReadValues(reader, schema.Count));
    }
}
