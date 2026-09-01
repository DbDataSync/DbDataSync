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
/// What one counter round-trip came back with.
/// </summary>
/// <param name="Value">The counter, in the same text encoding <c>ChangeWatermarks</c> uses, or null
/// when the source has no position to give — <c>fn_cdc_get_max_lsn()</c> before the capture job has
/// run.</param>
/// <param name="SourceTimeUtc">
/// When the engine says <paramref name="Value"/> committed. Filled on the CDC round-trip only, and
/// null everywhere else — not because Change Tracking has no mapping (it does, through
/// <c>dm_tran_commit_table</c>) but because CDC's costs nothing here: <c>fn_cdc_map_lsn_to_time</c>
/// rides along on the round-trip that already fetched the LSN, whereas the DMV lookup is a second
/// query worth making only when a lag figure is actually asked for. See phase 85.
/// </param>
public sealed record ChangeCounterReading(string? Value, DateTimeOffset? SourceTimeUtc = null);

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
    /// The current counter, and the time the source puts on it where the mechanism has one. Throws if
    /// the source is unreachable or the query fails; the gate treats that as a reason to fail open,
    /// not to stop.
    /// </summary>
    Task<ChangeCounterReading> FetchAsync(
        string connectionName, string sourceDatabase, string readerKind, CancellationToken cancellationToken);

    /// <summary>
    /// The time the source puts on an arbitrary stored position, rather than on its current one —
    /// through the engine's own mapping in both mechanisms: <c>fn_cdc_map_lsn_to_time</c> for an LSN,
    /// <c>dm_tran_commit_table</c> for a Change Tracking version.
    /// <para>
    /// The other half of an exact lag: the group's shared history row says where the database had got
    /// to, and this says where one mapping's own watermark sits on the same clock. It cannot be
    /// cached in <c>ChangeCheckHistory</c> beside the first, because the position is the mapping's and
    /// the row is the group's.
    /// </para>
    /// <para>
    /// **Null is a real answer, and the same one in both mechanisms**: the position is older than
    /// what the source still holds — outside <c>cdc.lsn_time_mapping</c>'s retained window, or aged
    /// out of the DMV's rolling one. Change Tracking's caller falls back to an estimate at that
    /// point; CDC's has none to fall back to. Also null for a reader kind with no counter at all.
    /// Throws on an unreachable source, like <see cref="FetchAsync"/>.
    /// </para>
    /// </summary>
    Task<DateTimeOffset?> MapSourceTimeAsync(
        string connectionName,
        string sourceDatabase,
        string readerKind,
        string value,
        CancellationToken cancellationToken);
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
    public async Task<ChangeCounterReading> FetchAsync(
        string connectionName, string sourceDatabase, string readerKind, CancellationToken cancellationToken)
    {
        var (connection, _) = await connections.OpenAsync(connectionName, cancellationToken);
        await using (connection)
        {
            connection.ChangeDatabase(sourceDatabase);

            switch (readerKind)
            {
                case MsSqlDriverKinds.Cdc:
                    // Both answers on one round-trip, on the connection that is already open and
                    // already in the right catalog. The time is only meaningful for the LSN it came
                    // from, so fetching them apart would be two positions and one of them stale.
                    if (await MsSqlCdcCatalog.GetMaxLsnAsync(connection, cancellationToken) is not { } lsn)
                        return new ChangeCounterReading(null);

                    return new ChangeCounterReading(
                        MsSqlCdcCatalog.ToWatermark(lsn),
                        await MsSqlCdcCatalog.MapLsnToTimeAsync(connection, lsn, cancellationToken));

                case MsSqlDriverKinds.ChangeTracking:
                    return new ChangeCounterReading(
                        (await MsSqlChangeTrackingReader.GetCurrentVersionAsync(connection, cancellationToken))
                            .ToString());

                default:
                    throw new InvalidOperationException(
                        $"'{readerKind}' has no database-wide change counter.");
            }
        }
    }

    public async Task<DateTimeOffset?> MapSourceTimeAsync(
        string connectionName,
        string sourceDatabase,
        string readerKind,
        string value,
        CancellationToken cancellationToken)
    {
        if (!ChangeCounters.IsGated(readerKind))
            return null;

        // Change Tracking's version is only a number until it is parsed; a stored value that is not
        // one is a corrupt watermark, and "cannot place it in time" is a better answer here than an
        // exception on a status screen.
        if (readerKind == MsSqlDriverKinds.ChangeTracking && !long.TryParse(value, out _))
            return null;

        var (connection, _) = await connections.OpenAsync(connectionName, cancellationToken);
        await using (connection)
        {
            connection.ChangeDatabase(sourceDatabase);

            return readerKind switch
            {
                MsSqlDriverKinds.Cdc => await MsSqlCdcCatalog.MapLsnToTimeAsync(
                    connection, MsSqlCdcCatalog.FromWatermark(value), cancellationToken),

                // Both mechanisms have an engine-side mapping after all — dm_tran_commit_table is
                // keyed by the same commit sequence number Change Tracking stamps versions with. The
                // database is switched for the same reason it is above: the DMV answers for the
                // current one.
                _ => await MsSqlChangeTrackingReader.MapVersionToTimeAsync(
                    connection, long.Parse(value), cancellationToken),
            };
        }
    }
}
