using System.Data.Common;
using System.Runtime.CompilerServices;
using DbDataSync.Core.Config;
using DbDataSync.Drivers.Abstractions;
using DbDataSync.Drivers.Generic;
using DbDataSync.Core.Sql;

namespace DbDataSync.Drivers.MsSql;

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
public sealed class MsSqlCdcReader : IChangeReader, IStatementPreview, IReadIntentDeclaring, IPositionCapturing
{
    public string Kind => MsSqlDriverKinds.Cdc;

    /// <summary>
    /// Declared beside the code that reads it. The shared descriptor rather than one of this reader's
    /// own: the setting means the same thing here as it does for Change Tracking, and declaring it once
    /// is what puts it in the SPA's reader-options form for CDC without any frontend change.
    /// </summary>
    public IReadOnlyList<ParameterDescriptor> Parameters { get; } = [BoundedRead.CappedDescriptor];

    /// <summary>CDC records a delete as its own change carrying the row's key, so a delete at the
    /// source reaches the writer as one.</summary>
    public bool DetectsDeletes => true;

    /// <summary>
    /// Phase 132: every row carries <c>(__$start_lsn, __$seqval)</c> as a sortable, per-row-unique
    /// key, and CDC's own <c>sys.fn_cdc_map_lsn_to_time</c> as its real change time — both projected by
    /// <see cref="MsSqlCdcStatement.BuildRead"/>. No other reader in this codebase can state either.
    /// </summary>
    public bool CapturesChangeOrder => true;

    /// <summary>
    /// All three that remain declarable per phase 101's retarget (<c>InitialLoad</c> is no longer a
    /// per-reader question — see <see cref="IReadIntentDeclaring"/>).
    /// <see cref="ReadIntent.ChangesFromEarliest"/> is the one this design exists for: it reads from
    /// <c>min_lsn</c> **inclusively**, via a distinct statement shape
    /// (<see cref="MsSqlCdcStatement.BuildRead"/>'s <c>inclusiveFloor</c>) rather than a decremented
    /// bound, so the change at the floor is never silently dropped and the reader's own
    /// <c>Compare(storedLsn, minLsn) &lt; 0</c> guard stays untouched.
    /// </summary>
    public IReadOnlySet<ReadIntent> SupportedIntents { get; } = new HashSet<ReadIntent>
    {
        ReadIntent.Changes, ReadIntent.ChangesFromEarliest, ReadIntent.ChangesFromLatest,
    };

    /// <summary>
    /// The current max LSN, exposed without an intent attached — exactly what
    /// <see cref="ReadIntent.ChangesFromLatest"/> above adopts, and genuinely readable without touching
    /// the change table: <c>sys.fn_cdc_get_max_lsn()</c> is a scalar function, not a row read.
    /// </summary>
    public async Task<CapturedPosition> CapturePositionAsync(
        DbConnection sourceConnection, SourceTableRef source, IReadOnlyDictionary<string, string> options,
        CancellationToken cancellationToken)
    {
        sourceConnection.ChangeDatabase(source.Database);
        var maxLsn = await MsSqlCdcCatalog.GetMaxLsnAsync(sourceConnection, cancellationToken)
            ?? throw new InvalidOperationException(
                $"Change Data Capture reports no maximum LSN in database '{source.Database}', which " +
                "means the capture job has not run. Start the SQL Server Agent job " +
                "'cdc.<database>_capture' before this mapping's position can be captured.");
        return new CapturedPosition(
            MsSqlCdcCatalog.ToWatermark(maxLsn), await MapTimeAsync(sourceConnection, maxLsn, cancellationToken));
    }

    public async Task<ReadResult> ReadChangesAsync(
        DbConnection sourceConnection,
        SourceTableRef source,
        string? previousWatermark,
        ReadIntent intent,
        IReadOnlyList<ColumnMapping> columnMappings,
        string mappingName,
        IReadOnlyList<CachedColumn> sourceColumns,
        IReadOnlyList<RelationshipConfig> relationships,
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

        // Phase 134: an InitialLoad pass never reaches this reader any more — RunExecutor routes it to
        // the Bulk Load pipeline instead, ahead of ever calling ReadChangesAsync, because this reader
        // implements IPositionCapturing. Every intent left to handle here has a stored position behind
        // it.
        if (intent == ReadIntent.ChangesFromLatest)
        {
            // Adopts the table as already-synced: the current max LSN becomes the new watermark and the
            // change table is never queried — not even for the empty window a caught-up ordinary pass
            // would still ask for.
            return new ReadResult(
                Empty(), MsSqlCdcCatalog.ToWatermark(maxLsn), diagnostics,
                NewWatermarkTimeUtc: await MapTimeAsync(sourceConnection, maxLsn, cancellationToken));
        }

        byte[] fromLsn;
        var inclusiveFloor = intent == ReadIntent.ChangesFromEarliest;
        if (inclusiveFloor)
        {
            // The floor itself, read inclusively — see SupportedIntents and BuildRead's inclusiveFloor.
            // Waited for on the same terms InitialLoad waits for it: not knowing the floor yet is not
            // the same as there being no changes.
            fromLsn = await WaitForCaptureFloorAsync(sourceConnection, instance, cancellationToken)
                ?? throw new InvalidOperationException(
                    $"Change Data Capture has not started capturing '{source.Schema}.{source.Table}' yet " +
                    $"(capture instance '{instance.CaptureInstance}' has no start position). This pass " +
                    "will succeed once the capture job has processed the enable — check that SQL Server " +
                    "Agent is running if it does not.");
        }
        else
        {
            var minLsn = await MsSqlCdcCatalog.GetMinLsnAsync(sourceConnection, instance.CaptureInstance, cancellationToken);
            fromLsn = MsSqlCdcCatalog.FromWatermark(previousWatermark!);
            if (minLsn is not null && MsSqlCdcCatalog.Compare(fromLsn, minLsn) < 0)
            {
                throw new PositionExpiredException(
                    $"{source.Schema}.{source.Table}", previousWatermark!,
                    MsSqlCdcCatalog.ToWatermark(minLsn), "Change Data Capture");
            }

            // Nothing new. Returning the stored position rather than the max keeps the next pass's
            // window starting exactly where this one would have.
            // Mapped even though the position has not moved: this is the pass that keeps a caught-up
            // mapping's cached time populated, including for a row written before phase 87 added the
            // column. A quiet mapping that only ever takes this branch would otherwise never acquire one.
            if (MsSqlCdcCatalog.Compare(fromLsn, maxLsn) >= 0)
                return new ReadResult(
                    Empty(), previousWatermark!, diagnostics,
                    NewWatermarkTimeUtc: await MapTimeAsync(sourceConnection, fromLsn, cancellationToken));
        }

        // Capped by default, unlike the watermark scan's opt-in: the ordering column here is the change
        // table's own clustered key, so bounding costs nothing to order by, and an uncapped pass over a
        // backlog nobody thought to configure is exactly the pass that holds a worker slot for an
        // unbounded stretch and — since phase 76's 1800s default command timeout — can now fail outright
        // rather than merely run long. Set the option to 0 to get the whole window back.
        //
        // The first pass above is deliberately not capped: it reads the table itself rather than the
        // change table, and a row cap there would be a partial full load with no resumable position to
        // record it — the same reasoning MsSqlChangeTrackingReader states.
        var maxRows = BoundedRead.Read(options, BoundedRead.DefaultMaxRows);
        var bounded = maxRows is null ? null : new BoundedReadPosition();

        return new ReadResult(
            ReadIncrementalAsync(
                sourceConnection, instance, fromLsn, maxLsn, columnMappings, maxRows, bounded, inclusiveFloor,
                cancellationToken),
            MsSqlCdcCatalog.ToWatermark(maxLsn),
            diagnostics,
            bounded,
            // For the unbounded case, and for a bounded pass that drains its whole window — both of
            // which store maxLsn. A pass the cap cuts short overrides this with the time of the
            // position it actually reached, in the same place it overrides the position itself.
            await MapTimeAsync(sourceConnection, maxLsn, cancellationToken));
    }

    /// <summary>
    /// The time the engine puts on an LSN this pass is about to store, or null if it will not say.
    /// <para>
    /// **Free here and only here.** The connection is open, already in the mapping's own database, and
    /// the position is the one being persisted — which is what lets a status screen later report lag
    /// out of the state database instead of asking this server again every thirty seconds (phase 87).
    /// </para>
    /// <para>
    /// **Never allowed to fail the pass.** A lag figure is a question about a replication; the
    /// replication is the thing itself. The same judgement <c>ReaderLagService</c> already makes about
    /// these calls — an unreachable or unhappy source means "no figure right now", not an error —
    /// moved to the call site where the value is now produced. The caller records a null and the next
    /// pass tries again.
    /// </para>
    /// </summary>
    private static async Task<DateTimeOffset?> MapTimeAsync(
        DbConnection connection, byte[] lsn, CancellationToken cancellationToken)
    {
        try
        {
            return await MsSqlCdcCatalog.MapLsnToTimeAsync(connection, lsn, cancellationToken);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
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

        var maxRows = BoundedRead.Read(request.Options, BoundedRead.DefaultMaxRows);
        List<PreviewParameter> parameters =
        [
            new PreviewParameter(
                "storedLsn", "binary(10)",
                MsSqlDialect.Instance.RenderLiteral(MsSqlCdcCatalog.FromWatermark(request.PreviousWatermark))),
            new PreviewParameter("toLsn", "binary(10)", MsSqlDialect.Instance.RenderLiteral(maxLsn)),
        ];
        if (maxRows is { } cap)
            parameters.Add(new PreviewParameter(BoundedRead.RowLimitParameter, "int", cap.ToString()));

        var declaredParameters = MsSqlDialect.Instance.RenderDeclarations(parameters);

        var notes = new List<string>
        {
            function == MsSqlCdcStatement.CdcFunction.NetChanges
                ? "Net changes: one row per key, whatever happened to it in the window."
                : "All changes: every intermediate change. This capture instance was created " +
                  "without @supports_net_changes, so net changes are not available for it.",
        };
        if (maxRows is { } limit)
        {
            notes.Add(
                $"Capped at {limit} rows, ties on __$start_lsn included — a pass cut short advances " +
                "only to its last row's LSN, and the rest arrives on the next pass. The tie is on the " +
                "LSN rather than on (__$start_lsn, __$seqval) because a stored position is an LSN: one " +
                "transaction's rows are never split across two passes, so a transaction larger than " +
                "the cap is delivered whole and over it.");
        }

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
                    column => RenderColumn(column, request.ColumnMappings), bounded: maxRows is not null),
                PreviewOrigin.BuiltIn,
                string.Join(" ", notes),
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

    private static async IAsyncEnumerable<ChangeRow> ReadIncrementalAsync(
        DbConnection connection,
        CdcCaptureInstance instance,
        byte[] storedLsn,
        byte[] maxLsn,
        IReadOnlyList<ColumnMapping> columnMappings,
        int? maxRows,
        BoundedReadPosition? bounded,
        bool inclusiveFloor,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var columns = ColumnsFor(instance, columnMappings);
        // The two ordering columns are appended after the mapped ones and before any bounded position
        // column — see BuildRead — and are real, schema-visible data (unlike the position column,
        // which stays out-of-band): this is what lets a staging provider find them by name and what
        // lets Scd2Writer process a key with more than one staged row in true source order.
        var schema = new ChangeSchema([.. columns, ChangeOrdering.OrderingColumn, ChangeOrdering.ChangedAtColumn]);

        using var cmd = connection.CreateTimedCommand();
        cmd.CommandText = MsSqlCdcStatement.BuildRead(
            instance.CaptureInstance, FunctionFor(instance), columns,
            column => RenderColumn(column, columnMappings), bounded: maxRows is not null,
            inclusiveFloor: inclusiveFloor);
        cmd.AddParameter("@storedLsn", storedLsn);
        cmd.AddParameter("@toLsn", maxLsn);
        if (maxRows is { } limit)
            cmd.AddParameter($"@{BoundedRead.RowLimitParameter}", limit);

        // Appended after the mapped columns — see BuildRead — so nothing below shifts when the cap is on.
        var positionOrdinal = MsSqlCdcStatement.PositionOrdinal(schema.Count);
        long rowsRead = 0;
        byte[]? lastLsn = null;

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            if (bounded is not null)
            {
                rowsRead++;
                lastLsn = reader.GetFieldValue<byte[]>(positionOrdinal);
            }

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

        // Only a pass the cap actually cut short reports an intermediate position. One that drained its
        // window advances to maxLsn instead — the position it fixed for itself before reading, which is
        // still one it genuinely reached. Reporting the last row's LSN there would leave a quiet table's
        // watermark frozen at its final change, and a frozen watermark eventually falls below
        // fn_cdc_get_min_lsn and expires.
        //
        // WITH TIES means the engine may return more than the cap, so the test is >=. Every row sharing
        // lastLsn is guaranteed to be in this batch — including the rest of its transaction — which is
        // what makes stopping here resumable rather than lossy, given that the next pass resumes at
        // fn_cdc_increment_lsn(lastLsn) and will never look at that LSN again.
        if (bounded is not null && lastLsn is not null && maxRows is { } cap && rowsRead >= cap)
        {
            bounded.Reached = MsSqlCdcCatalog.ToWatermark(lastLsn);

            // Beside the position, from the same row, on the connection that just streamed it — the
            // cap's own answer to the mapping done up front for maxLsn, which this pass is not
            // storing. See ReadResult.WatermarkTimeAfterRead for why the pair travels together.
            bounded.ReachedTimeUtc = await MapTimeAsync(connection, lastLsn, cancellationToken);
        }
    }
}
