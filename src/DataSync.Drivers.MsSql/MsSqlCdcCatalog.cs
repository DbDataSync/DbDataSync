using System.Data.Common;
using DataSync.Drivers.Generic;

namespace DataSync.Drivers.MsSql;

/// <param name="CaptureInstance">CDC names its functions after the capture instance, not the table.</param>
/// <param name="SupportsNetChanges">Whether the instance was created with
/// <c>@supports_net_changes = 1</c>, which is what decides which of the two read shapes is available.</param>
/// <param name="CapturedColumns">The columns the instance captures, in the change table's own order.
/// A capture instance can cover a subset, and a column added to the table after capture was enabled
/// is not in it — which is a real way to silently stop replicating a column.</param>
public sealed record CdcCaptureInstance(
    string CaptureInstance, bool SupportsNetChanges, IReadOnlyList<string> CapturedColumns);

/// <summary>
/// The <c>cdc.*</c> catalog lookups the CDC reader needs. Separate from
/// <see cref="MsSqlSchemaQueries"/> because these only exist in a database where CDC is enabled, and
/// a missing <c>cdc</c> schema is an answer ("not enabled here") rather than an error.
/// </summary>
public static class MsSqlCdcCatalog
{
    /// <summary>
    /// The capture instance for a table, or null when the table is not captured.
    /// <para>
    /// A table can have two instances — that is how SQL Server supports a schema change without losing
    /// capture — and when it does, this takes the more recently created one, because the older one
    /// exists to be drained and retired. Reported by the reader either way, so an operator mid-schema-
    /// change is told which they are reading.
    /// </para>
    /// </summary>
    public static async Task<CdcCaptureInstance?> FindCaptureInstanceAsync(
        DbConnection connection, string schema, string table, CancellationToken cancellationToken)
    {
        if (!await CdcIsEnabledAsync(connection, cancellationToken))
            return null;

        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT TOP (1) ct.capture_instance, ct.supports_net_changes
            FROM cdc.change_tables ct
            JOIN sys.tables t ON t.object_id = ct.source_object_id
            JOIN sys.schemas s ON s.schema_id = t.schema_id
            WHERE s.name = @schema AND t.name = @table
            ORDER BY ct.create_date DESC;
            """;
        cmd.AddParameter("@schema", schema);
        cmd.AddParameter("@table", table);

        string captureInstance;
        bool supportsNetChanges;
        await using (var reader = await cmd.ExecuteReaderAsync(cancellationToken))
        {
            if (!await reader.ReadAsync(cancellationToken))
                return null;

            captureInstance = reader.GetString(0);
            supportsNetChanges = reader.GetBoolean(1);
        }

        return new CdcCaptureInstance(
            captureInstance, supportsNetChanges,
            await CapturedColumnsAsync(connection, captureInstance, cancellationToken));
    }

    /// <summary>
    /// Whether this database has CDC on at all. Checked before touching <c>cdc.*</c>, because on a
    /// database where it was never enabled those objects do not exist and the query fails with an
    /// invalid-object error — which reads as a broken driver rather than as "CDC is not set up here".
    /// </summary>
    public static async Task<bool> CdcIsEnabledAsync(DbConnection connection, CancellationToken cancellationToken)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT is_cdc_enabled FROM sys.databases WHERE database_id = DB_ID();";
        var result = await cmd.ExecuteScalarAsync(cancellationToken);
        return result is not (null or DBNull) && Convert.ToBoolean(result);
    }

    private static async Task<IReadOnlyList<string>> CapturedColumnsAsync(
        DbConnection connection, string captureInstance, CancellationToken cancellationToken)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT cc.column_name
            FROM cdc.captured_columns cc
            JOIN cdc.change_tables ct ON ct.object_id = cc.object_id
            WHERE ct.capture_instance = @instance
            ORDER BY cc.column_ordinal;
            """;
        cmd.AddParameter("@instance", captureInstance);

        var columns = new List<string>();
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            columns.Add(reader.GetString(0));
        return columns;
    }

    /// <summary>
    /// The high-water mark for this database, or null when the capture job has not run.
    /// <para>
    /// **Null is not "no changes".** <c>fn_cdc_get_max_lsn()</c> returns null when the capture job has
    /// never run or has been stopped, which from the reader's side is indistinguishable from a quiet
    /// source — and treating it as a position rather than as an absence is how a replication sits
    /// silently doing nothing while the source changes underneath it.
    /// </para>
    /// </summary>
    public static async Task<byte[]?> GetMaxLsnAsync(DbConnection connection, CancellationToken cancellationToken)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT sys.fn_cdc_get_max_lsn();";
        var result = await cmd.ExecuteScalarAsync(cancellationToken);
        return result is null or DBNull ? null : (byte[])result;
    }

    /// <summary>
    /// The oldest position this capture instance can still serve — CDC's analogue of Change Tracking's
    /// minimum valid version. Null when it is not established yet.
    /// <para>
    /// <c>fn_cdc_get_min_lsn</c> answers with **all zeroes**, not null, in the window between
    /// <c>sp_cdc_enable_table</c> and the capture job getting round to the enable. Zero is not a
    /// position: read as one it sorts below every real LSN, which makes every stored watermark look
    /// expired. Normalised to null here so the one caller cannot get that wrong.
    /// </para>
    /// </summary>
    public static async Task<byte[]?> GetMinLsnAsync(
        DbConnection connection, string captureInstance, CancellationToken cancellationToken)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT sys.fn_cdc_get_min_lsn(@instance);";
        cmd.AddParameter("@instance", captureInstance);
        var result = await cmd.ExecuteScalarAsync(cancellationToken);
        if (result is null or DBNull)
            return null;

        var lsn = (byte[])result;
        return lsn.All(b => b == 0) ? null : lsn;
    }

    /// <summary>
    /// An LSN as it is stored in a watermark: hex, without a prefix.
    /// <para>
    /// <c>ReadResult.NewWatermark</c> is an opaque string and the state store keeps it as text, so
    /// nothing outside this driver changes to hold an LSN. Hex rather than the decimal a
    /// <c>binary(10)</c> would convert to, because it is what SQL Server itself prints and therefore
    /// what an operator comparing the two will be looking at.
    /// </para>
    /// </summary>
    public static string ToWatermark(byte[] lsn) => Convert.ToHexString(lsn);

    public static byte[] FromWatermark(string watermark) => Convert.FromHexString(watermark);

    /// <summary>Ordering for <c>binary(10)</c> values, which SQL Server compares as unsigned
    /// big-endian and .NET does not compare at all.</summary>
    public static int Compare(byte[] left, byte[] right)
    {
        for (var i = 0; i < Math.Min(left.Length, right.Length); i++)
        {
            if (left[i] != right[i])
                return left[i].CompareTo(right[i]);
        }

        return left.Length.CompareTo(right.Length);
    }
}
