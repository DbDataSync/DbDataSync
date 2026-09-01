using System.Data.Common;
using System.Runtime.CompilerServices;
using DataSync.Core.Config;
using DataSync.Drivers.Abstractions;
using DataSync.Drivers.Generic;
using DataSync.Core.Sql;

namespace DataSync.Drivers.MsSql;

/// <summary>
/// Reads changes via SQL Server Change Data Capture.
///
/// <para>
/// **CDC is not an upgrade to Change Tracking**, and the names invite exactly that conclusion. CT
/// reports the *net* change since a version — one row per key. CDC reports every intermediate change
/// harvested from the transaction log. For mirroring a table, CT's semantics are the better ones: a
/// row updated fifty times between passes is one row from CT and fifty from CDC, and the target only
/// needs the final state. CT stays the default.
/// </para>
///
/// <para>
/// What CDC uniquely offers this codebase is **read consistency**. The CT reader joins
/// <c>CHANGETABLE</c> to the base table for current values, and between the two the base table can
/// move — 28,000 anomalous rows out of 312,000 under load, which is the bug phase 12 spent its effort
/// on and which produced the opt-in snapshot-isolation mode, the pooled-connection isolation leak, and
/// the error-3952 path. CDC has no such join: the change table already holds the column values as of
/// the change. Nothing to race, no snapshot transaction, no 3952.
/// </para>
///
/// <para>
/// So the reader **prefers net changes and falls back to all changes**, and says which it used. That
/// combination — CT's collapsing with CDC's join-free consistency — is the best change-tracking mode
/// available on this engine for this tool's purpose.
/// </para>
///
/// <para>
/// Like the CT reader, this only ever queries; enabling capture is the provisioner's job and an
/// operator's decision.
/// </para>
/// </summary>
public sealed class MsSqlCdcReader : IChangeReader, IStatementPreview
{
    public string Kind => MsSqlDriverKinds.Cdc;

    /// <summary>CDC records a delete as its own change carrying the row's key, so a delete at the
    /// source reaches the writer as one.</summary>
    public bool DetectsDeletes => true;

    public async Task<ReadResult> ReadChangesAsync(
        DbConnection sourceConnection,
        SourceTableRef source,
        string? previousWatermark,
        IReadOnlyList<ColumnMapping> columnMappings,
        IReadOnlyDictionary<string, string> options,
        CancellationToken cancellationToken)
    {
        sourceConnection.ChangeDatabase(source.Database);

        var instance = await MsSqlCdcCatalog.FindCaptureInstanceAsync(
            sourceConnection, source.Schema, source.Table, cancellationToken)
            ?? throw new InvalidOperationException(
                $"Change Data Capture is not enabled for table '{source.Schema}.{source.Table}' in " +
                $"database '{source.Database}'.");

        var maxLsn = await MsSqlCdcCatalog.GetMaxLsnAsync(sourceConnection, cancellationToken)
            ?? throw new InvalidOperationException(
                $"Change Data Capture reports no maximum LSN in database '{source.Database}', which " +
                "means the capture job has not run. A stopped capture job looks exactly like a quiet " +
                "source from here, so this is reported rather than read as 'no changes' — start the " +
                "SQL Server Agent job 'cdc.<database>_capture'.");

        var diagnostics = new ReadDiagnostics();

        if (previousWatermark is null)
        {
            // The floor has to be known before a first pass can store a position, and it is not known
            // the instant a table is enabled — the capture job records it when it processes the
            // enable. A first pass that stored the database's max LSN instead would store a position
            // *below* the instance's own floor, and every pass after it would read that as expired
            // history when nothing had expired. So this waits for the floor rather than guessing at
            // it, and says so if it never arrives.
            var floor = await WaitForCaptureFloorAsync(sourceConnection, instance, cancellationToken)
                ?? throw new InvalidOperationException(
                    $"Change Data Capture has not started capturing '{source.Schema}.{source.Table}' yet " +
                    $"(capture instance '{instance.CaptureInstance}' has no start position). This pass " +
                    "will succeed once the capture job has processed the enable — check that SQL Server " +
                    "Agent is running if it does not.");

            // CDC's change table only holds what has happened since capture was enabled, so a table
            // that already had rows would otherwise start half-replicated with nothing to say so.
            var rows = ReadFullLoadAsync(
                sourceConnection, source, SourceProjection.Render(MsSqlDialect.Instance, columnMappings),
                cancellationToken);

            // The later of the two: the floor can be ahead of what the job has scanned, and the max
            // can be ahead of the floor once the job has caught up.
            var start = MsSqlCdcCatalog.Compare(floor, maxLsn) > 0 ? floor : maxLsn;
            return new ReadResult(rows, MsSqlCdcCatalog.ToWatermark(start), diagnostics);
        }

        var minLsn = await MsSqlCdcCatalog.GetMinLsnAsync(sourceConnection, instance.CaptureInstance, cancellationToken);
        var storedLsn = MsSqlCdcCatalog.FromWatermark(previousWatermark);
        if (minLsn is not null && MsSqlCdcCatalog.Compare(storedLsn, minLsn) < 0)
        {
            throw new PositionExpiredException(
                $"{source.Schema}.{source.Table}", previousWatermark,
                MsSqlCdcCatalog.ToWatermark(minLsn), "Change Data Capture");
        }

        // Nothing new. Returning the stored position rather than the max keeps the next pass's window
        // starting exactly where this one would have.
        if (MsSqlCdcCatalog.Compare(storedLsn, maxLsn) >= 0)
            return new ReadResult(Empty(), previousWatermark, diagnostics);

        return new ReadResult(
            ReadIncrementalAsync(sourceConnection, instance, storedLsn, maxLsn, columnMappings, cancellationToken),
            MsSqlCdcCatalog.ToWatermark(maxLsn),
            diagnostics);
    }

    /// <summary>
    /// The oldest position this capture instance can serve, waiting briefly for it to be established.
    /// <para>
    /// Bounded and short: this is the gap between <c>sp_cdc_enable_table</c> returning and the capture
    /// job recording the instance's start, which is one scan interval. Waiting forever would turn a
    /// stopped Agent into a hung pass, and that is a thing to report rather than to sit through.
    /// </para>
    /// </summary>
    private static async Task<byte[]?> WaitForCaptureFloorAsync(
        DbConnection connection, CdcCaptureInstance instance, CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(30);
        while (true)
        {
            if (await MsSqlCdcCatalog.GetMinLsnAsync(connection, instance.CaptureInstance, cancellationToken) is { } floor)
                return floor;

            if (DateTimeOffset.UtcNow >= deadline)
                return null;

            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
        }
    }

    /// <inheritdoc cref="MsSqlCdcStatement.CdcFunction"/>
    private static MsSqlCdcStatement.CdcFunction FunctionFor(CdcCaptureInstance instance) =>
        instance.SupportsNetChanges
            ? MsSqlCdcStatement.CdcFunction.NetChanges
            : MsSqlCdcStatement.CdcFunction.AllChanges;

    public async Task<IReadOnlyList<PreviewStatement>> DescribeAsync(
        PreviewRequest request, CancellationToken cancellationToken)
    {
        request.Connection.ChangeDatabase(request.Source.Database);

        var instance = await MsSqlCdcCatalog.FindCaptureInstanceAsync(
            request.Connection, request.Source.Schema, request.Source.Table, cancellationToken);

        if (instance is null)
        {
            return
            [
                new PreviewStatement(
                    PreviewStages.SourceRead, "Incremental read", null, PreviewOrigin.BuiltIn,
                    $"Change Data Capture is not enabled for '{request.Source.Schema}.{request.Source.Table}', " +
                    "so this pass would fail before issuing a statement."),
            ];
        }

        if (request.PreviousWatermark is null)
        {
            return
            [
                new PreviewStatement(
                    PreviewStages.SourceRead,
                    "Full load — no CDC position stored yet, so the next pass reads every row",
                    MsSqlCdcStatement.BuildFullLoad(
                        request.Source.Schema, request.Source.Table,
                        SourceProjection.Render(MsSqlDialect.Instance, request.ColumnMappings),
                        request.Source.Filter),
                    PreviewOrigin.BuiltIn),
            ];
        }

        var function = FunctionFor(instance);

        // Not resolved by ReadChangesAsync until it actually runs — but a preview that showed
        // @storedLsn/@toLsn as bare placeholders would leave an admin pasting this into SSMS with
        // nothing to declare them from. Fetched here purely to describe the statement, same as
        // ReadIncrementalAsync would fetch it to run one.
        var maxLsn = await MsSqlCdcCatalog.GetMaxLsnAsync(request.Connection, cancellationToken);
        if (maxLsn is null)
        {
            return
            [
                new PreviewStatement(
                    PreviewStages.SourceRead, "Incremental read", null, PreviewOrigin.BuiltIn,
                    $"Change Data Capture reports no maximum LSN in database '{request.Source.Database}', " +
                    "which means the capture job has not run — this pass would fail before issuing a " +
                    "statement."),
            ];
        }

        var declaredParameters = MsSqlDialect.Instance.RenderDeclarations(
        [
            new PreviewParameter(
                "storedLsn", "binary(10)",
                MsSqlDialect.Instance.RenderLiteral(MsSqlCdcCatalog.FromWatermark(request.PreviousWatermark))),
            new PreviewParameter("toLsn", "binary(10)", MsSqlDialect.Instance.RenderLiteral(maxLsn)),
        ]);

        return
        [
            new PreviewStatement(
                PreviewStages.SourceRead,
                "Ask the source for its current maximum LSN, which bounds this pass",
                MsSqlCdcCatalog.MaxLsnStatement,
                PreviewOrigin.BuiltIn,
                "Taken before the changes are read, not derived from them — the window ends where the " +
                "capture job had reached when the pass began, and anything captured after that arrives " +
                "on the next one."),
            new PreviewStatement(
                PreviewStages.SourceRead,
                $"Incremental read of changes after LSN {request.PreviousWatermark}",
                MsSqlCdcStatement.BuildRead(
                    instance.CaptureInstance, function, ColumnsFor(instance, request.ColumnMappings),
                    column => RenderColumn(column, request.ColumnMappings)),
                PreviewOrigin.BuiltIn,
                function == MsSqlCdcStatement.CdcFunction.NetChanges
                    ? "Net changes: one row per key, whatever happened to it in the window."
                    : "All changes: every intermediate change. This capture instance was created " +
                      "without @supports_net_changes, so net changes are not available for it.",
                declaredParameters),
        ];
    }

    /// <summary>
    /// The mapped columns, restricted to what the capture instance actually captures.
    /// <para>
    /// A capture instance can cover a subset of the table, and a column added after capture was
    /// enabled is not in it. Selecting one from the change table fails with an invalid-column error
    /// that names the change table rather than the mapping, which is a long way from the actual
    /// problem — so the mismatch is reported by the reader instead.
    /// </para>
    /// </summary>
    private static IReadOnlyList<string> ColumnsFor(
        CdcCaptureInstance instance, IReadOnlyList<ColumnMapping> columnMappings)
    {
        var captured = instance.CapturedColumns.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var wanted = columnMappings.Count == 0
            ? instance.CapturedColumns
            : [.. columnMappings.Select(m => m.SourceColumn)];

        var missing = wanted.Where(c => !captured.Contains(c)).ToList();
        if (missing.Count > 0)
        {
            throw new InvalidOperationException(
                $"Capture instance '{instance.CaptureInstance}' does not capture " +
                $"{string.Join(", ", missing.Select(m => $"'{m}'"))}. A capture instance covers the " +
                "columns it was created with, so a column added to the table since then is not in it " +
                "— capture has to be re-enabled for the table to pick it up.");
        }

        return wanted;
    }

    private static string RenderColumn(string column, IReadOnlyList<ColumnMapping> columnMappings)
    {
        var mapping = columnMappings.FirstOrDefault(
            m => string.Equals(m.SourceColumn, column, StringComparison.OrdinalIgnoreCase));

        // No join here, so {{column}} resolves to the bare quoted name — unlike the CT reader, where
        // it has to be `base.[Col]` to be unambiguous against CHANGETABLE's own copy of the key.
        return string.IsNullOrWhiteSpace(mapping?.Transform)
            ? SqlIdentifier.Quote(column)
            : $"{mapping!.Transform!.Replace(ColumnMapping.ColumnToken, SqlIdentifier.Quote(column))} " +
              $"AS {SqlIdentifier.Quote(column)}";
    }

    private static async IAsyncEnumerable<ChangeRow> Empty()
    {
        await Task.CompletedTask;
        yield break;
    }

    private static async IAsyncEnumerable<ChangeRow> ReadFullLoadAsync(
        DbConnection connection, SourceTableRef source, string projection,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var cmd = connection.CreateTimedCommand();
        cmd.CommandText = MsSqlCdcStatement.BuildFullLoad(source.Schema, source.Table, projection, source.Filter);

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        var schema = ResultSetSchema.From(reader);
        while (await reader.ReadAsync(cancellationToken))
            yield return new ChangeRow(ChangeOperation.Insert, schema, ResultSetSchema.ReadValues(reader, schema.Count));
    }

    private static async IAsyncEnumerable<ChangeRow> ReadIncrementalAsync(
        DbConnection connection,
        CdcCaptureInstance instance,
        byte[] storedLsn,
        byte[] maxLsn,
        IReadOnlyList<ColumnMapping> columnMappings,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var columns = ColumnsFor(instance, columnMappings);
        var schema = new ChangeSchema(columns);

        using var cmd = connection.CreateTimedCommand();
        cmd.CommandText = MsSqlCdcStatement.BuildRead(
            instance.CaptureInstance, FunctionFor(instance), columns,
            column => RenderColumn(column, columnMappings));
        cmd.AddParameter("@storedLsn", storedLsn);
        cmd.AddParameter("@toLsn", maxLsn);

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var operation = MsSqlCdcStatement.Operation(reader.GetInt32(MsSqlCdcStatement.OperationOrdinal)) switch
            {
                MsSqlCdcStatement.ChangeOperationCode.Insert => ChangeOperation.Insert,
                MsSqlCdcStatement.ChangeOperationCode.Update => ChangeOperation.Update,
                _ => ChangeOperation.Delete,
            };

            // Pre-sized, like every other reader here. Unlike the CT reader there is no populated/
            // unpopulated distinction to make: CDC's delete row carries the values as of the delete,
            // so every column is real.
            var values = new object?[schema.Count];
            for (var i = 0; i < schema.Count; i++)
            {
                var ordinal = i + 1; // past __$operation
                values[i] = reader.IsDBNull(ordinal) ? null : reader.GetValue(ordinal);
            }

            yield return new ChangeRow(operation, schema, values);
        }
    }
}
