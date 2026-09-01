using DataSync.Core.Config;
using DataSync.Drivers.Abstractions;
using DataSync.Drivers.Generic;

namespace DataSync.Api.Services;

/// <summary>
/// Where one table mapping's incremental reads come from, and under what key its position is stored.
/// </summary>
/// <param name="ConnectionName">With <paramref name="SourceDatabase"/> and
/// <paramref name="ReaderKind"/>, the <c>ChangeCheckHistory</c> group this mapping shares with every
/// other mapping polling the same database by the same mechanism.</param>
/// <param name="WatermarkKey">The <c>SourceTable</c> half of this mapping's <c>ChangeWatermarks</c>
/// key — spelled in the source dialect, which is why resolving one needs the driver registry.</param>
public sealed record ChangeSource(
    string ConnectionName, string SourceDatabase, string ReaderKind, string WatermarkKey);

/// <summary>
/// Answers "which source group is this mapping in, and where is its own position kept" — the one
/// question both <see cref="ChangePollingGate"/> and <see cref="ReaderLagService"/> have to get
/// identically right.
/// <para>
/// Shared rather than duplicated because the two are the same claim seen from either side: the gate
/// predicts whether a mapping is behind its group's counter, and lag says by how much. Two copies of
/// this resolution could drift into grouping a mapping one way for the skip decision and another for
/// the number reported about it, and the second would look like a lag bug rather than a grouping one.
/// </para>
/// </summary>
public sealed class ChangeSourceResolver(ConfigRepository configRepository, DriverRegistry driverRegistry)
{
    /// <summary>
    /// The mapping's reader kind, and its source group where it has one.
    /// <para>
    /// A null <c>Source</c> means this mapping is none of a gate's or a lag report's business — a
    /// reader kind with no database-wide counter, or a mapping with more than one source, which has
    /// no single position to speak of. The reader kind comes back either way, because "this mechanism
    /// reports no lag" is an answer a report has to be able to give by name rather than as a blank.
    /// </para>
    /// <para>
    /// Throws whatever loading the config throws; both callers treat that as "cannot answer" rather
    /// than as a failure of their own.
    /// </para>
    /// </summary>
    public (string ReaderKind, ChangeSource? Source) Describe(
        ReplicationTaskConfig task, string mappingName)
    {
        var mapping = configRepository.LoadTableMapping(task.Name, mappingName);
        var readerKind = PipelineResolution.ReaderKind(null, task, mapping);
        if (!ChangeCounters.IsGated(readerKind) || mapping.Sources.Count != 1)
            return (readerKind, null);

        var source = EndpointResolution.ResolveSource(task, mapping.Sources[0]);

        // The dialect from the connection's configured driver rather than from an open connection —
        // the same read phase 74 moved ResyncService onto, and for the same reason: the key of a
        // watermark must be computable when the source is down, which is exactly when the gate is
        // about to fail open and needs to have got this far.
        var connection = configRepository.LoadConnection(source.ConnectionName);
        if (driverRegistry.Get(connection.DriverType) is not IDialectProvider dialect)
            return (readerKind, null);

        return (readerKind, new ChangeSource(
            source.ConnectionName,
            source.Database,
            readerKind,
            WatermarkKey.Build(source, dialect.Dialect)));
    }
}
