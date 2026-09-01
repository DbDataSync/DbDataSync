using DataSync.Drivers.MsSql;

namespace DataSync.Api.Services;

/// <summary>
/// The database-wide change counters this gate understands, and how to compare two of them — see
/// phase 75.
/// <para>
/// The kinds are the reader kinds themselves (<c>MsSqlDriverKinds.Cdc</c>,
/// <c>MsSqlDriverKinds.ChangeTracking</c>) rather than a new enum beside them. A second vocabulary
/// for "which mechanism" would need a mapping back to the first at every boundary, and the first is
/// already what the config states, what <c>WorkQueue</c>/<c>TaskRuns</c> record, and what the
/// provisioner dispatches on.
/// </para>
/// </summary>
public static class ChangeCounters
{
    /// <summary>Whether a reader kind has a database-wide counter this gate can poll. Every other
    /// kind — the generic Watermark reader, batch reload, trigger audit — is scheduled exactly as it
    /// was before this phase.</summary>
    public static bool IsGated(string readerKind) =>
        readerKind is MsSqlDriverKinds.Cdc or MsSqlDriverKinds.ChangeTracking;

    /// <summary>
    /// Compares two counter values of the same kind, in the encoding both this gate and
    /// <c>ChangeWatermarks</c> store them in.
    /// <para>
    /// Each mechanism's own comparison, not a second one invented here: <c>MsSqlCdcCatalog.Compare</c>
    /// for an LSN, which is a big-endian byte sequence and orders as one, and a plain numeric compare
    /// for Change Tracking's version. Comparing a hex LSN as a string would happen to work for equal
    /// lengths and quietly stop when one is shorter; comparing a version as text would order 9 above
    /// 10.
    /// </para>
    /// </summary>
    public static int Compare(string readerKind, string left, string right) => readerKind switch
    {
        MsSqlDriverKinds.Cdc =>
            MsSqlCdcCatalog.Compare(MsSqlCdcCatalog.FromWatermark(left), MsSqlCdcCatalog.FromWatermark(right)),
        MsSqlDriverKinds.ChangeTracking =>
            long.Parse(left).CompareTo(long.Parse(right)),
        _ => throw new InvalidOperationException($"'{readerKind}' has no database-wide change counter."),
    };
}

/// <summary>
/// One source round-trip: the current database-wide change counter for a connection, in the same
/// text encoding <c>ChangeWatermarks</c> uses.
/// <para>
/// An interface so the gate's decision logic can be tested against a source that is not a real SQL
/// Server. It is the only part of the gate that does I/O, which is deliberate — everything else it
/// does is arithmetic over local state.
/// </para>
/// </summary>
public interface IChangeCounterSource
{
    /// <summary>
    /// The counter, or null when the source has no position to give — <c>fn_cdc_get_max_lsn()</c>
    /// before the capture job has run. Throws if the source is unreachable or the query fails; the
    /// gate treats that as a reason to fail open, not to stop.
    /// </summary>
    Task<string?> FetchAsync(
        string connectionName, string sourceDatabase, string readerKind, CancellationToken cancellationToken);
}

/// <summary>
/// Fetches a counter over a real connection, through the same statements the readers issue.
/// <para>
/// **The connection is switched to the mapping's own database first**, because both readers do:
/// <c>MsSqlCdcReader</c> and <c>MsSqlChangeTrackingReader</c> each open with
/// <c>ChangeDatabase(source.Database)</c> before asking for their counter, and
/// <c>fn_cdc_get_max_lsn()</c> and <c>CHANGE_TRACKING_CURRENT_VERSION()</c> both answer for whatever
/// database is current. A gate that skipped that step would read the connection's default catalog
/// and compare its counter against a watermark measured in a different database — which for a
/// connection whose default is quiet would suppress a mapping that has work waiting, silently, for
/// as long as the two stayed out of step. Matching the reader is the whole contract here.
/// </para>
/// </summary>
public sealed class DriverChangeCounterSource(DriverConnectionFactory connections) : IChangeCounterSource
{
    public async Task<string?> FetchAsync(
        string connectionName, string sourceDatabase, string readerKind, CancellationToken cancellationToken)
    {
        var (connection, _) = await connections.OpenAsync(connectionName, cancellationToken);
        await using (connection)
        {
            connection.ChangeDatabase(sourceDatabase);

            return readerKind switch
            {
                MsSqlDriverKinds.Cdc =>
                    await MsSqlCdcCatalog.GetMaxLsnAsync(connection, cancellationToken) is { } lsn
                        ? MsSqlCdcCatalog.ToWatermark(lsn)
                        : null,
                MsSqlDriverKinds.ChangeTracking =>
                    (await MsSqlChangeTrackingReader.GetCurrentVersionAsync(connection, cancellationToken))
                        .ToString(),
                _ => throw new InvalidOperationException(
                    $"'{readerKind}' has no database-wide change counter."),
            };
        }
    }
}
