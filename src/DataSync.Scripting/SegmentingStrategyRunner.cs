using System.Data.Common;
using System.Globalization;
using DataSync.Core.Config;
using DataSync.Drivers.Abstractions;
using DataSync.Scripting.Abstractions;
using DuckDB.NET.Data;

namespace DataSync.Scripting;

/// <summary>
/// Runs a <see cref="SegmentingStrategyConfig"/> and returns the candidates it proposes — see phase 58.
/// <para>
/// One runner for all four authoring paths, because the difference between them is only *where the
/// four columns come from*. Every path ends at the same place: a label, a half-open range, and whether
/// the candidate is selected. Keeping the row-to-candidate translation in one place is what stops a
/// DuckDB strategy and a source-SQL strategy quietly disagreeing about what an exclusive end means.
/// </para>
/// </summary>
public sealed class SegmentingStrategyRunner(ScriptHost scriptHost)
{
    /// <summary>
    /// The candidates <paramref name="strategy"/> proposes.
    /// <para>
    /// <paramref name="sourceConnection"/> and <paramref name="targetConnection"/> may be null when the
    /// caller has none open — which a DuckDB strategy never notices, and which the other three refuse
    /// plainly rather than dereferencing.
    /// </para>
    /// </summary>
    public async Task<IReadOnlyList<SegmentCandidate>> RunAsync(
        SegmentingStrategyConfig strategy,
        SegmentingContext context,
        DbConnection? sourceConnection,
        DbConnection? targetConnection,
        CancellationToken cancellationToken)
    {
        var column = strategy.Column;
        if (string.IsNullOrWhiteSpace(column) && strategy.Kind != SegmentingStrategyKind.Script)
            throw new InvalidOperationException(
                $"Segmenting strategy '{strategy.Name}' does not say which column its ranges are over. " +
                "The query returns bounds; only you know what they bound.");

        return strategy.Kind switch
        {
            SegmentingStrategyKind.DuckDb =>
                await RunDuckDbAsync(strategy, column!, cancellationToken),

            SegmentingStrategyKind.SourceSql => await RunSqlAsync(
                strategy, column!, Require(sourceConnection, strategy, "source"), cancellationToken),

            SegmentingStrategyKind.TargetSql => await RunSqlAsync(
                strategy, column!, Require(targetConnection, strategy, "target"), cancellationToken),

            SegmentingStrategyKind.Script => RunScript(strategy, context),

            _ => throw new InvalidOperationException(
                $"Segmenting strategy '{strategy.Name}' has an unknown kind '{strategy.Kind}'."),
        };
    }

    private static DbConnection Require(DbConnection? connection, SegmentingStrategyConfig strategy, string side) =>
        connection ?? throw new InvalidOperationException(
            $"Segmenting strategy '{strategy.Name}' queries the {side}, but no {side} connection is available here.");

    /// <summary>
    /// A single read-only <c>SELECT</c> against an ephemeral in-memory instance that is attached to
    /// nothing.
    /// <para>
    /// Worth being explicit that this does not reopen <c>state-store-concurrency.md</c>'s "not
    /// adoptable yet" verdict on DuckDB. That was about the state store needing
    /// <c>UPDATE</c>/<c>DELETE</c>/upsert over the Quack remote protocol. This is one query, no
    /// persistence, no <c>ATTACH</c>, and a database that ceases to exist when the connection closes —
    /// a materially smaller thing, evaluated on its own.
    /// </para>
    /// </summary>
    private static async Task<IReadOnlyList<SegmentCandidate>> RunDuckDbAsync(
        SegmentingStrategyConfig strategy, string column, CancellationToken cancellationToken)
    {
        await using var connection = new DuckDBConnection("DataSource=:memory:");
        await connection.OpenAsync(cancellationToken);
        return await ReadCandidatesAsync(strategy, column, connection, cancellationToken);
    }

    private static async Task<IReadOnlyList<SegmentCandidate>> RunSqlAsync(
        SegmentingStrategyConfig strategy, string column, DbConnection connection, CancellationToken cancellationToken) =>
        await ReadCandidatesAsync(strategy, column, connection, cancellationToken);

    private static async Task<IReadOnlyList<SegmentCandidate>> ReadCandidatesAsync(
        SegmentingStrategyConfig strategy, string column, DbConnection connection, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(strategy.Sql))
            throw new InvalidOperationException($"Segmenting strategy '{strategy.Name}' has no SQL to run.");

        await using var command = connection.CreateCommand();
        command.CommandText = strategy.Sql;

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        var ordinals = ResolveOrdinals(strategy, reader);
        var candidates = new List<SegmentCandidate>();

        while (await reader.ReadAsync(cancellationToken))
        {
            var start = Format(reader.GetValue(ordinals.RangeStart));
            var end = Format(reader.GetValue(ordinals.RangeEnd));
            var label = reader.IsDBNull(ordinals.Label) ? null : reader.GetValue(ordinals.Label)?.ToString();

            // Absent means nothing is pre-selected. A strategy that does not say which candidates
            // matter is proposing, not deciding — and unattended that has to mean "do nothing" rather
            // than "do everything".
            var selected = ordinals.Selected >= 0
                          && !reader.IsDBNull(ordinals.Selected)
                          && Convert.ToBoolean(reader.GetValue(ordinals.Selected), CultureInfo.InvariantCulture);

            candidates.Add(new SegmentCandidate(new RangeSegment(column, start, end, label), selected));
        }

        return candidates;
    }

    private static (int Label, int RangeStart, int RangeEnd, int Selected) ResolveOrdinals(
        SegmentingStrategyConfig strategy, DbDataReader reader)
    {
        var byName = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < reader.FieldCount; i++)
            byName[reader.GetName(i)] = i;

        var missing = SegmentingStrategyColumns.Required.Where(c => !byName.ContainsKey(c)).ToList();
        if (missing.Count > 0)
            throw new InvalidOperationException(
                $"Segmenting strategy '{strategy.Name}' must return column(s) {string.Join(", ", missing)}. " +
                $"It returned: {string.Join(", ", byName.Keys)}.");

        return (
            byName[SegmentingStrategyColumns.Label],
            byName[SegmentingStrategyColumns.RangeStart],
            byName[SegmentingStrategyColumns.RangeEnd],
            byName.GetValueOrDefault(SegmentingStrategyColumns.Selected, -1));
    }

    /// <summary>
    /// A bound value as the invariant text a <see cref="RangeSegment"/> carries.
    /// <para>
    /// Round-trip <c>"O"</c> for dates specifically, because a segment's bounds are re-bound against
    /// the source's own column type later and a locale-formatted date would be parsed by whatever the
    /// server's locale happens to be. This is the same reason <c>WatermarkValue.Format</c> exists.
    /// </para>
    /// </summary>
    private static string Format(object? value) => value switch
    {
        null or DBNull => throw new InvalidOperationException(
            "A segmenting strategy returned a null range bound; a range needs both ends."),
        DateTime date => date.ToString("O", CultureInfo.InvariantCulture),
        DateTimeOffset offset => offset.ToString("O", CultureInfo.InvariantCulture),
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? "",
    };

    private IReadOnlyList<SegmentCandidate> RunScript(SegmentingStrategyConfig strategy, SegmentingContext context)
    {
        if (string.IsNullOrWhiteSpace(strategy.ScriptName))
            throw new InvalidOperationException($"Segmenting strategy '{strategy.Name}' names no script.");

        // By name, like a verification check's builder and the ScriptedQuery reader's — which script
        // answers a particular question is a property of that question, not something to inherit from
        // a connection.
        var script = scriptHost.Resolve<ISegmentingStrategy>(strategy.ScriptName);
        return script.ProposeSegments(context);
    }
}
