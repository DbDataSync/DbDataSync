using System.Data;
using System.Data.Common;
using System.Runtime.CompilerServices;
using Microsoft.Data.SqlClient;
using DataSync.Core.Config;
using DataSync.Drivers.Abstractions;

using DataSync.Drivers.Generic;

namespace DataSync.Drivers.MsSql;

/// <summary>
/// Reads changes via SQL Server Change Tracking (architecture/detailed-design.md §3.5 — the
/// recommended starting reader, simpler to enable than CDC). Requires Change Tracking already
/// enabled on the database and table by an operator with the necessary permissions; this driver only
/// ever queries it, never enables it (keeps the app's own DB permissions to read-only + VIEW CHANGE
/// TRACKING, per §6's least-privilege guidance).
/// </summary>
public sealed class MsSqlChangeTrackingReader : IChangeReader, IStatementPreview
{
    /// <summary>
    /// Opt-in: read CHANGETABLE and the source table inside one snapshot transaction, which is the
    /// pairing SQL Server's own Change Tracking guidance recommends — without it the two are read at
    /// different instants and a concurrent delete can leave a reported insert/update with no row
    /// behind it. Off by default because it requires
    /// <c>ALTER DATABASE &lt;source&gt; SET ALLOW_SNAPSHOT_ISOLATION ON</c>, and this driver never
    /// changes database settings (see the type doc). Without it the reader stays correct by skipping
    /// those rows instead.
    /// </summary>
    public const string SnapshotIsolationOption = "snapshotIsolation";

    /// <summary>Declared beside the code that reads it, so the two cannot describe different things.</summary>
    public IReadOnlyList<ParameterDescriptor> Parameters { get; } =
    [
        new()
        {
            Name = SnapshotIsolationOption,
            Label = "Snapshot isolation",
            Description =
                "Reads the change window inside a snapshot transaction, so a row changing mid-pass " +
                "cannot be read in two states. Requires the source database to allow snapshot isolation.",
            Type = ParameterType.Bool,
            Default = "false",
        },
    ];

    /// <summary>Snapshot isolation transaction failed because it isn't allowed in this database.</summary>
    private const int SnapshotIsolationNotAllowedError = 3952;

    public string Kind => MsSqlDriverKinds.ChangeTracking;

    /// <summary>Change Tracking records a deleted row's key in CHANGETABLE, so a delete at the source
    /// reaches the writer as one — the whole reason to prefer this reader over a watermark scan.</summary>
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

        var targetVersion = await GetCurrentVersionAsync(sourceConnection, cancellationToken);

        if (previousWatermark is not null)
        {
            var minValidVersion = await GetMinValidVersionAsync(sourceConnection, source, cancellationToken);
            if (long.Parse(previousWatermark) < minValidVersion)
            {
                throw new InvalidOperationException(
                    $"Change Tracking history for '{source.Schema}.{source.Table}' no longer covers " +
                    $"watermark '{previousWatermark}' (minimum valid version is {minValidVersion}). " +
                    "A full resync is required — clear the stored watermark for this table.");
            }
        }

        var diagnostics = new ReadDiagnostics();
        var rows = previousWatermark is null
            ? ReadFullLoadAsync(
                sourceConnection, source, SourceProjection.Render(MsSqlDialect.Instance, columnMappings), cancellationToken)
            : ReadIncrementalAsync(
                sourceConnection, source, long.Parse(previousWatermark), targetVersion,
                UseSnapshotIsolation(options), columnMappings, diagnostics, cancellationToken);

        return new ReadResult(rows, targetVersion.ToString(), diagnostics);
    }

    /// <summary>
    /// What this reader would issue next, built by the same calls <see cref="ReadChangesAsync"/> makes.
    /// Which of the two statements it describes depends on the stored watermark, exactly as the run
    /// does — a preview that always showed the first-pass form would be wrong for every pass after
    /// the first, which is all of them.
    /// </summary>
    public async Task<IReadOnlyList<PreviewStatement>> DescribeAsync(
        PreviewRequest request, CancellationToken cancellationToken)
    {
        request.Connection.ChangeDatabase(request.Source.Database);
        var projection = SourceProjection.Render(MsSqlDialect.Instance, request.ColumnMappings);

        if (request.PreviousWatermark is null)
        {
            return
            [
                new PreviewStatement(
                    PreviewStages.SourceRead,
                    "Full load — no change-tracking version stored yet, so the next pass reads every row",
                    MsSqlChangeTrackingStatement.BuildFullLoad(
                        request.Source.Schema, request.Source.Table, projection, request.Source.Filter),
                    PreviewOrigin.BuiltIn),
            ];
        }

        var columns = await MsSqlSchemaQueries.GetColumnsAsync(
            request.Connection, request.Source.Schema, request.Source.Table, cancellationToken);
        var pkColumns = columns.Where(c => c.IsPrimaryKey).Select(c => c.Name).ToList();
        if (pkColumns.Count == 0)
        {
            return
            [
                new PreviewStatement(
                    PreviewStages.SourceRead, "Incremental read", null, PreviewOrigin.BuiltIn,
                    $"Table '{request.Source.Schema}.{request.Source.Table}' has no primary key; " +
                    "Change Tracking requires one, so this pass would fail before issuing a statement."),
            ];
        }

        return
        [
            new PreviewStatement(
                PreviewStages.SourceRead,
                $"Incremental read of changes after version {request.PreviousWatermark}",
                MsSqlChangeTrackingStatement.BuildIncremental(
                    request.Source.Schema, request.Source.Table, pkColumns,
                    columns.Where(c => !c.IsPrimaryKey).Select(c => c.Name).ToList(),
                    column => RenderNonKeyColumn(column, request.ColumnMappings)),
                PreviewOrigin.BuiltIn,
                UseSnapshotIsolation(request.Options) ? "Runs in a snapshot-isolation transaction." : null),
        ];
    }

    private static bool UseSnapshotIsolation(IReadOnlyDictionary<string, string> options) =>
        options.TryGetValue(SnapshotIsolationOption, out var raw)
        && (string.Equals(raw, "true", StringComparison.OrdinalIgnoreCase) || raw == "1");

    private static async Task<long> GetCurrentVersionAsync(DbConnection connection, CancellationToken cancellationToken)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT CHANGE_TRACKING_CURRENT_VERSION();";
        var result = await cmd.ExecuteScalarAsync(cancellationToken);
        return result is null or DBNull ? 0 : Convert.ToInt64(result);
    }

    private static async Task<long> GetMinValidVersionAsync(
        DbConnection connection, SourceTableRef source, CancellationToken cancellationToken)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT CHANGE_TRACKING_MIN_VALID_VERSION(OBJECT_ID(@qualifiedName));";
        cmd.AddParameter("@qualifiedName", $"{source.Schema}.{source.Table}");
        var result = await cmd.ExecuteScalarAsync(cancellationToken);
        if (result is null or DBNull)
            throw new InvalidOperationException(
                $"Change Tracking is not enabled for table '{source.Schema}.{source.Table}' in database '{source.Database}'.");
        return Convert.ToInt64(result);
    }

    private static async IAsyncEnumerable<ChangeRow> ReadFullLoadAsync(
        DbConnection connection, SourceTableRef source, string projection,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = MsSqlChangeTrackingStatement.BuildFullLoad(
            source.Schema, source.Table, projection, source.Filter);

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        var schema = ResultSetSchema.From(reader);
        while (await reader.ReadAsync(cancellationToken))
            yield return new ChangeRow(ChangeOperation.Insert, schema, ResultSetSchema.ReadValues(reader, schema.Count));
    }

    private static async IAsyncEnumerable<ChangeRow> ReadIncrementalAsync(
        DbConnection connection,
        SourceTableRef source,
        long previousVersion,
        long targetVersion,
        bool useSnapshotIsolation,
        IReadOnlyList<ColumnMapping> columnMappings,
        ReadDiagnostics diagnostics,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var columns = await MsSqlSchemaQueries.GetColumnsAsync(connection, source.Schema, source.Table, cancellationToken);
        var pkColumns = columns.Where(c => c.IsPrimaryKey).Select(c => c.Name).ToList();
        if (pkColumns.Count == 0)
            throw new InvalidOperationException(
                $"Table '{source.Schema}.{source.Table}' has no primary key; Change Tracking requires one.");
        var nonKeyColumns = columns.Where(c => !c.IsPrimaryKey).Select(c => c.Name).ToList();

        // Matches the select list's tail, so a result-set ordinal maps to a schema ordinal by
        // subtracting the two leading bookkeeping columns.
        var schema = new ChangeSchema([.. pkColumns, .. nonKeyColumns]);

        using var cmd = connection.CreateCommand();
        // The statement joins the table under the alias `base`, so a transform's {{column}} has to
        // resolve to `base.[Col]` and not to a bare name — which for a primary key column would be
        // ambiguous against CHANGETABLE's own copy. This is the reason the token exists.
        cmd.CommandText = MsSqlChangeTrackingStatement.BuildIncremental(
            source.Schema, source.Table, pkColumns, nonKeyColumns,
            column => RenderNonKeyColumn(column, columnMappings));
        cmd.AddParameter("@previousVersion", previousVersion);
        cmd.AddParameter("@targetVersion", targetVersion);

        // Opening is separated from the read loop because a `yield return` may not sit inside a
        // try/catch — only inside try/finally. Everything that can throw the snapshot-not-allowed
        // error happens here, where it can be turned into an actionable message.
        DbTransaction? transaction = null;
        DbDataReader reader;
        try
        {
            if (useSnapshotIsolation)
            {
                transaction = await connection.BeginTransactionAsync(IsolationLevel.Snapshot, cancellationToken);
                cmd.Transaction = transaction;
            }

            reader = await cmd.ExecuteReaderAsync(cancellationToken);
        }
        catch (SqlException ex) when (useSnapshotIsolation && ex.Number == SnapshotIsolationNotAllowedError)
        {
            if (transaction is not null)
                await transaction.DisposeAsync();
            await RestoreDefaultIsolationLevelAsync(connection);

            throw SnapshotIsolationNotAllowed(source, ex);
        }

        try
        {
            await using (reader)
            {
                while (true)
                {
                    // A snapshot transaction against a database that doesn't allow it fails on first
                    // *data access*, which for this query is the first ReadAsync — not BEGIN and not
                    // ExecuteReader. That is inside the streaming loop, where a `yield return` forbids
                    // a try/catch around the whole body, so only the read itself is wrapped.
                    bool hasRow;
                    try
                    {
                        hasRow = await reader.ReadAsync(cancellationToken);
                    }
                    catch (SqlException ex) when (useSnapshotIsolation && ex.Number == SnapshotIsolationNotAllowedError)
                    {
                        throw SnapshotIsolationNotAllowed(source, ex);
                    }

                    if (!hasRow)
                        break;

                    var operation = reader.GetString(MsSqlChangeTrackingStatement.OperationOrdinal) switch
                    {
                        "I" => ChangeOperation.Insert,
                        "U" => ChangeOperation.Update,
                        "D" => ChangeOperation.Delete,
                        var op => throw new InvalidOperationException($"Unknown SYS_CHANGE_OPERATION '{op}'."),
                    };

                    var sourceRowGone = reader.GetInt32(MsSqlChangeTrackingStatement.BaseMissingOrdinal) == 1;

                    // An insert/update whose source row has already been deleted carries no values at
                    // all — every non-key column is a LEFT JOIN NULL, indistinguishable from real
                    // data. Skip it rather than applying nonsense: the delete that removed it bumped
                    // that key's change version above this run's targetVersion, so the very filter
                    // that let this stale row through guarantees the delete is still pending and
                    // arrives on a later pass. See
                    // architecture/planning/done/task-run-errors-during-high-volume-workload.md.
                    if (sourceRowGone && operation != ChangeOperation.Delete)
                    {
                        diagnostics.RowsSkippedSourceRowGone++;
                        continue;
                    }

                    // Pre-sized, like every other reader in this driver. Without it the dictionary
                    // resizes 3 -> 7 -> 17 -> 37 -> 79 on the way to a wide row, discarding each
                    // intermediate: measured at 5,424 B/row against 2,584 B/row for a 50-column
                    // table. This is the incremental path, so it is the one that runs constantly.
                    // Deleted rows are gone from the source table — the LEFT JOIN yields NULLs for
                    // every non-key column, which would be indistinguishable from a real NULL value.
                    // Only key columns are reliable for deletes, so the rest of the row stays unset
                    // (see ChangeRow's XML doc).
                    var populated = operation == ChangeOperation.Delete
                        ? pkColumns.Count
                        : schema.Count;

                    var values = new object?[schema.Count];
                    for (var i = 0; i < populated; i++)
                    {
                        var ordinal = MsSqlChangeTrackingStatement.FirstKeyOrdinal + i;
                        values[i] = reader.IsDBNull(ordinal) ? null : reader.GetValue(ordinal);
                    }

                    yield return new ChangeRow(operation, schema, values);
                }
            }

            if (transaction is not null)
                await transaction.CommitAsync(cancellationToken);
        }
        finally
        {
            if (transaction is not null)
                await transaction.DisposeAsync();
            if (useSnapshotIsolation)
                await RestoreDefaultIsolationLevelAsync(connection);
        }
    }

    private static InvalidOperationException SnapshotIsolationNotAllowed(SourceTableRef source, Exception inner) =>
        new($"The '{SnapshotIsolationOption}' reader option needs snapshot isolation enabled on the source " +
            $"database. Run: ALTER DATABASE [{source.Database}] SET ALLOW_SNAPSHOT_ISOLATION ON; " +
            "or remove the option — the reader is correct without it, it just skips rows whose source row is " +
            "deleted mid-read instead of reading them consistently.", inner);

    /// <summary>
    /// Puts the session back to READ COMMITTED after a snapshot read.
    /// <para>
    /// Not optional housekeeping. <c>SET TRANSACTION ISOLATION LEVEL</c> is session state, and
    /// Microsoft.Data.SqlClient pools the underlying session — so a connection returned to the pool
    /// still carrying SNAPSHOT hands it to whoever draws it next. That next caller may be a different
    /// table mapping, or the writer, neither of which asked for snapshot isolation; and if the
    /// database does not allow it, their very first statement fails with error 3952 for no reason
    /// they could diagnose. Found exactly that way: tests that never enabled the option started
    /// failing on their seed INSERT after a sibling test used it.
    /// </para>
    /// </summary>
    private static async Task RestoreDefaultIsolationLevelAsync(DbConnection connection)
    {
        try
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "SET TRANSACTION ISOLATION LEVEL READ COMMITTED;";
            await cmd.ExecuteNonQueryAsync(CancellationToken.None);
        }
        catch (DbException)
        {
            // The connection is already broken; letting this replace the real failure would hide it.
        }
    }

    /// <summary>
    /// A non-key column as it appears in the incremental statement's select list: the mapping's
    /// transform if it has one, otherwise the plain <c>base.[Col]</c> reference.
    /// </summary>
    private static string RenderNonKeyColumn(string column, IReadOnlyList<ColumnMapping> columnMappings)
    {
        var baseReference = $"base.{SqlIdentifier.Quote(column)}";
        var mapping = columnMappings.FirstOrDefault(
            m => string.Equals(m.SourceColumn, column, StringComparison.OrdinalIgnoreCase)
                 && !string.IsNullOrWhiteSpace(m.Transform));

        if (mapping is null)
            return baseReference;

        var expression = mapping.Transform!.Contains(ColumnMapping.ColumnToken, StringComparison.Ordinal)
            ? mapping.Transform.Replace(ColumnMapping.ColumnToken, baseReference, StringComparison.Ordinal)
            : mapping.Transform;

        return $"{expression} AS {SqlIdentifier.Quote(column)}";
    }
}