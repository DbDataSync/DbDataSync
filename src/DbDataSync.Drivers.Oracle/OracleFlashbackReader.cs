using System.Data.Common;
using System.Runtime.CompilerServices;
using DbDataSync.Core.Config;
using DbDataSync.Drivers.Abstractions;
using DbDataSync.Drivers.Generic;
using Oracle.ManagedDataAccess.Client;
using DbDataSync.Core.Sql;

namespace DbDataSync.Drivers.Oracle;

/// <summary>Oracle-only Kind names — not <see cref="GenericDriverKinds"/>, since nothing about
/// <see cref="OracleFlashbackReader"/> is shared with any other engine.</summary>
public static class OracleDriverKinds
{
    public const string Flashback = "OracleFlashback";
}

/// <summary>
/// Reads changes via Oracle's Flashback Version Query — <c>SELECT ... FROM t VERSIONS BETWEEN SCN a AND
/// b</c> — the one genuinely new reader this phase adds; every other reader the Oracle driver registers
/// is <c>DbDataSync.Drivers.Generic</c>'s.
/// <para>
/// **Non-consuming by construction.** Unlike the trigger-audit options, there is no shadow table to
/// prune — this reader implements no <see cref="IPositionAcknowledging"/> at all, because there is
/// nothing here to acknowledge.
/// </para>
/// <para>
/// **A delete's row carries its real values, not nulls.** This is a genuine, positive difference from
/// <c>TriggerAuditReader</c>: a shadow-table delete's non-key columns come back null because the base
/// row is gone by the time it is read (the LEFT JOIN finds nothing), but a Flashback version <em>is</em>
/// the row as it stood immediately before deletion — every mapped column is populated for every
/// operation, delete included. Confirmed against a live server, not assumed from the design doc.
/// </para>
/// <para>
/// **SCN capture needs its own grant, confirmed the hard way.** <c>SELECT CURRENT_SCN FROM
/// V$DATABASE</c> — the design doc's first idea — needs a dictionary-view grant
/// (<c>SELECT_CATALOG_ROLE</c> or better) an ordinary app user does not have by default; a plain
/// connection got <c>ORA-00942</c> (table or view does not exist — Oracle's usual way of hiding
/// something a user cannot see rather than naming it). <c>DBMS_FLASHBACK.GET_SYSTEM_CHANGE_NUMBER()</c>
/// — the doc's "smaller-grant" alternative — needs its own explicit <c>EXECUTE</c> grant for the same
/// reason (also <c>ORA-00904</c>, invalid identifier, until granted); once granted it returns a precise,
/// monotonic SCN and is what this reader actually uses. <c>TIMESTAMP_TO_SCN(SYSTIMESTAMP)</c> needs no
/// grant at all but was rejected after testing: its SCN-to-timestamp mapping has coarse granularity —
/// two captures milliseconds apart came back with the *same* SCN in testing — which is exactly the
/// precision a position-capture mechanism cannot afford to get wrong.
/// </para>
/// </summary>
public sealed class OracleFlashbackReader(OracleDialect dialect)
    : IChangeReader, IStatementPreview, IReadIntentDeclaring, IPositionCapturing
{
    public string Kind => OracleDriverKinds.Flashback;

    /// <summary>A deleted row's final version carries its column values as they stood — see this
    /// class's own doc comment.</summary>
    public bool DetectsDeletes => true;

    /// <summary>
    /// Not <see cref="ReadIntent.ChangesFromEarliest"/>: unlike <c>TriggerAuditReader</c>'s real
    /// <c>MIN(DS_Seq)</c>, Flashback has no queryable "oldest surviving position" — its floor is
    /// whatever the undo tablespace happens to still retain, governed by <c>UNDO_RETENTION</c> and
    /// tablespace size, neither of which is a position this reader can read and hand back. Offering the
    /// intent would mean guessing at a floor rather than reading one.
    /// </summary>
    public IReadOnlySet<ReadIntent> SupportedIntents { get; } =
        new HashSet<ReadIntent> { ReadIntent.Changes, ReadIntent.ChangesFromLatest };

    /// <summary>The current SCN, via <c>DBMS_FLASHBACK.GET_SYSTEM_CHANGE_NUMBER()</c> — see this
    /// class's own doc comment for why not <c>V$DATABASE</c> or <c>TIMESTAMP_TO_SCN</c>. No engine
    /// mapping from an SCN to a time is queried here, so <see cref="CapturedPosition.PositionTimeUtc"/>
    /// is always null, the same posture <c>TriggerAuditReader</c> takes for its own sequence.</summary>
    public async Task<CapturedPosition> CapturePositionAsync(
        DbConnection sourceConnection, SourceTableRef source, IReadOnlyDictionary<string, string> options,
        CancellationToken cancellationToken)
    {
        var scn = await GetCurrentScnAsync(sourceConnection, cancellationToken);
        return new CapturedPosition(scn.ToString(), PositionTimeUtc: null);
    }

    public async Task<ReadResult> ReadChangesAsync(
        DbConnection sourceConnection,
        SourceTableRef source,
        string? previousWatermark,
        ReadIntent intent,
        IReadOnlyList<ColumnMapping> columnMappings,
        string mappingName,
        IReadOnlyList<CachedColumn> sourceColumns,
        IReadOnlyDictionary<string, string> options,
        CancellationToken cancellationToken)
    {
        // Computed up front, before any row is read — the same rule every reader's NewWatermark
        // follows, and the reason a Flashback pass is safe to run concurrently with source writes: a
        // row committed after this SCN was fixed simply is not in the window this pass reads, and
        // arrives on the next pass instead of racing this one.
        var target = await GetCurrentScnAsync(sourceConnection, cancellationToken);
        var diagnostics = new ReadDiagnostics();

        // Phase 134: an InitialLoad pass never reaches this reader — RunExecutor routes it to the Bulk
        // Load pipeline instead, ahead of ever calling ReadChangesAsync, because this reader implements
        // IPositionCapturing. Every intent left to handle here has a stored position behind it.
        if (intent == ReadIntent.ChangesFromLatest)
            return new ReadResult(Empty(), target.ToString(), diagnostics);

        var previous = long.Parse(previousWatermark!);

        // Already caught up to the SCN this pass fixed for itself.
        if (previous >= target)
            return new ReadResult(Empty(), target.ToString(), diagnostics);

        return new ReadResult(
            ReadVersionsAsync(sourceConnection, source, previous, target, columnMappings, cancellationToken),
            target.ToString(),
            diagnostics);
    }

    public async Task<IReadOnlyList<PreviewStatement>> DescribeAsync(
        PreviewRequest request, CancellationToken cancellationToken)
    {
        if (request.PreviousWatermark is null)
        {
            return
            [
                new PreviewStatement(
                    PreviewStages.SourceRead,
                    "Full load — no Flashback position stored yet, so the next pass reads every row",
                    SourceProjection.Render(dialect, request.ColumnMappings) is var projection
                        ? $"SELECT {projection} FROM {dialect.QualifyTable(request.Source.Schema, request.Source.Table)};"
                        : null,
                    PreviewOrigin.BuiltIn),
            ];
        }

        var target = await GetCurrentScnAsync(request.Connection, cancellationToken);
        List<PreviewParameter> parameters =
        [
            new("previousScn", "number", request.PreviousWatermark!),
            new("targetScn", "number", target.ToString()),
        ];

        return
        [
            new PreviewStatement(
                PreviewStages.SourceRead,
                $"Flashback read of changes after SCN {request.PreviousWatermark}",
                BuildVersionsQuery(request.Source.Schema, request.Source.Table, request.ColumnMappings),
                PreviewOrigin.BuiltIn,
                "Non-consuming — nothing here is pruned or acknowledged. Bounded by UNDO_RETENTION: a " +
                "position older than the source's surviving undo fails with a clear reload-required error " +
                "rather than a silent gap.",
                dialect.RenderDeclarations(parameters)),
        ];
    }

    /// <summary><c>ORA-01555</c> (snapshot too old) and <c>ORA-30052</c> (invalid lower-bound SCN) are
    /// the source discarding the history this position needed — mapped to the shared
    /// <see cref="PositionExpiredException"/> like every other position-expired case, rather than
    /// surfaced as Oracle's own raw error text.</summary>
    private async IAsyncEnumerable<ChangeRow> ReadVersionsAsync(
        DbConnection connection,
        SourceTableRef source,
        long previous,
        long target,
        IReadOnlyList<ColumnMapping> columnMappings,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var cmd = connection.CreateTimedCommand();
        cmd.CommandText = BuildVersionsQuery(source.Schema, source.Table, columnMappings);
        cmd.Parameters.Add(new OracleParameter("previousScn", OracleDbType.Decimal) { Value = (decimal)previous });
        cmd.Parameters.Add(new OracleParameter("targetScn", OracleDbType.Decimal) { Value = (decimal)target });

        DbDataReader reader;
        try
        {
            reader = await cmd.ExecuteReaderAsync(cancellationToken);
        }
        catch (OracleException ex) when (ex.Number is 1555 or 30052)
        {
            throw new PositionExpiredException(
                $"{source.Schema}.{source.Table}", previous.ToString(),
                "unavailable — Oracle exposes no queryable oldest-surviving SCN the way a trigger-audit " +
                "shadow table's own MIN(seq) does; retention is governed by UNDO_RETENTION and undo " +
                "tablespace size, not by a value this reader can read directly",
                "Flashback Version Query");
        }

        await using (reader)
        {
            // VERSIONS_OPERATION and VERSIONS_STARTSCN trail the mapped columns rather than lead them,
            // so ResultSetSchema.FromLeading can describe the change set's own shape without them —
            // the same trailing-bookkeeping-column shape WatermarkReader's bounded read uses.
            var opOrdinal = reader.FieldCount - 2;
            var schema = ResultSetSchema.FromLeading(reader, opOrdinal);

            while (await reader.ReadAsync(cancellationToken))
            {
                var operation = reader.GetString(opOrdinal) switch
                {
                    "I" => ChangeOperation.Insert,
                    "U" => ChangeOperation.Update,
                    "D" => ChangeOperation.Delete,
                    var op => throw new InvalidOperationException(
                        $"Flashback Version Query for '{source.Schema}.{source.Table}' returned an unknown " +
                        $"VERSIONS_OPERATION '{op}'."),
                };

                yield return new ChangeRow(operation, schema, ResultSetSchema.ReadValues(reader, schema.Count));
            }
        }
    }

    private string BuildVersionsQuery(string schema, string table, IReadOnlyList<ColumnMapping> columnMappings)
    {
        var qualifiedTable = dialect.QualifyTable(schema, table);
        var projection = SourceProjection.Render(dialect, columnMappings);

        return $"""
            SELECT {projection}, VERSIONS_OPERATION, VERSIONS_STARTSCN
            FROM {qualifiedTable}
            VERSIONS BETWEEN SCN {dialect.ParameterReference("previousScn")} AND {dialect.ParameterReference("targetScn")}
            WHERE VERSIONS_OPERATION IS NOT NULL
            ORDER BY VERSIONS_STARTSCN
            """;
    }

    private static async Task<long> GetCurrentScnAsync(DbConnection connection, CancellationToken cancellationToken)
    {
        using var cmd = connection.CreateTimedCommand();
        cmd.CommandText = "SELECT DBMS_FLASHBACK.GET_SYSTEM_CHANGE_NUMBER() FROM dual";

        try
        {
            var result = await cmd.ExecuteScalarAsync(cancellationToken);
            return Convert.ToInt64(result);
        }
        catch (DbException ex)
        {
            throw new InvalidOperationException(
                "Could not read the current SCN via DBMS_FLASHBACK.GET_SYSTEM_CHANGE_NUMBER(). This " +
                "connection's user needs EXECUTE on DBMS_FLASHBACK — a grant Oracle does not hand out " +
                $"by default. ({ex.Message})", ex);
        }
    }

    private static async IAsyncEnumerable<ChangeRow> Empty()
    {
        await Task.CompletedTask;
        yield break;
    }
}
