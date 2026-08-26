using System.Data;
using System.Data.Common;
using System.Runtime.CompilerServices;
using Microsoft.Data.SqlClient;
using DataSync.Core.Config;
using DataSync.Drivers.Abstractions;

namespace DataSync.Drivers.MsSql;

/// <summary>
/// Reads changes via SQL Server Change Tracking (architecture/detailed-design.md §3.5 — the
/// recommended starting reader, simpler to enable than CDC). Requires Change Tracking already
/// enabled on the database and table by an operator with the necessary permissions; this driver only
/// ever queries it, never enables it (keeps the app's own DB permissions to read-only + VIEW CHANGE
/// TRACKING, per §6's least-privilege guidance).
/// </summary>
public sealed class MsSqlChangeTrackingReader : IChangeReader
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

    /// <summary>Snapshot isolation transaction failed because it isn't allowed in this database.</summary>
    private const int SnapshotIsolationNotAllowedError = 3952;

    public string Kind => MsSqlDriverKinds.ChangeTracking;

    public async Task<ReadResult> ReadChangesAsync(
        DbConnection sourceConnection,
        SourceTableRef source,
        string? previousWatermark,
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
            ? ReadFullLoadAsync(sourceConnection, source, cancellationToken)
            : ReadIncrementalAsync(
                sourceConnection, source, long.Parse(previousWatermark), targetVersion,
                UseSnapshotIsolation(options), diagnostics, cancellationToken);

        return new ReadResult(rows, targetVersion.ToString(), diagnostics);
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
        DbConnection connection, SourceTableRef source, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var cmd = connection.CreateCommand();
        var filterClause = string.IsNullOrWhiteSpace(source.Filter) ? "" : $" WHERE {source.Filter}";
        // source.Filter is an admin-authored raw predicate from TableMappingConfig, not end-user
        // input — see the type's XML doc. It cannot be parameterized since it's an arbitrary
        // boolean expression, not a value.
        cmd.CommandText =
            $"SELECT * FROM {SqlIdentifier.Quote(source.Schema)}.{SqlIdentifier.Quote(source.Table)}{filterClause};";

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            yield return new ChangeRow(ChangeOperation.Insert, ReadRowValues(reader));
    }

    private static async IAsyncEnumerable<ChangeRow> ReadIncrementalAsync(
        DbConnection connection,
        SourceTableRef source,
        long previousVersion,
        long targetVersion,
        bool useSnapshotIsolation,
        ReadDiagnostics diagnostics,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var columns = await MsSqlSchemaQueries.GetColumnsAsync(connection, source.Schema, source.Table, cancellationToken);
        var pkColumns = columns.Where(c => c.IsPrimaryKey).Select(c => c.Name).ToList();
        if (pkColumns.Count == 0)
            throw new InvalidOperationException(
                $"Table '{source.Schema}.{source.Table}' has no primary key; Change Tracking requires one.");
        var nonKeyColumns = columns.Where(c => !c.IsPrimaryKey).Select(c => c.Name).ToList();

        using var cmd = connection.CreateCommand();
        cmd.CommandText = MsSqlChangeTrackingStatement.BuildIncremental(source.Schema, source.Table, pkColumns, nonKeyColumns);
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
                    var values = new Dictionary<string, object?>(pkColumns.Count + nonKeyColumns.Count);
                    for (var i = 0; i < pkColumns.Count; i++)
                    {
                        var ordinal = MsSqlChangeTrackingStatement.FirstKeyOrdinal + i;
                        values[reader.GetName(ordinal)] = reader.IsDBNull(ordinal) ? null : reader.GetValue(ordinal);
                    }

                    // Deleted rows are gone from the source table — the LEFT JOIN yields NULLs for
                    // every non-key column, which would be indistinguishable from a real NULL value.
                    // Only key columns are reliable for deletes (see ChangeRow's XML doc).
                    if (operation != ChangeOperation.Delete)
                    {
                        var firstNonKey = MsSqlChangeTrackingStatement.FirstKeyOrdinal + pkColumns.Count;
                        for (var ordinal = firstNonKey; ordinal < reader.FieldCount; ordinal++)
                            values[reader.GetName(ordinal)] = reader.IsDBNull(ordinal) ? null : reader.GetValue(ordinal);
                    }

                    yield return new ChangeRow(operation, values);
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

    private static Dictionary<string, object?> ReadRowValues(DbDataReader reader)
    {
        var values = new Dictionary<string, object?>(reader.FieldCount);
        for (var i = 0; i < reader.FieldCount; i++)
            values[reader.GetName(i)] = reader.IsDBNull(i) ? null : reader.GetValue(i);
        return values;
    }
}
