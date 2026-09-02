using DataSync.Core.Config;
using DataSync.Core.Git;
using DataSync.Drivers.Abstractions;

namespace DataSync.Api.Services;

/// <summary>
/// Re-reads a mapping's source and target catalogs and overwrites the shape cached on the mapping —
/// the only thing in the system that writes <see cref="TableMappingConfig.SourceColumns"/> and
/// <see cref="TableMappingConfig.TargetColumns"/> other than a save whose table changed.
/// <para>
/// It goes through <see cref="MetadataService"/> rather than a driver catalog of its own, which is
/// what makes "Refresh" show the operator the same answer the column-mapping picker beside it shows:
/// one introspection path, so a connection with a bound <c>metadataProvider</c> script is honoured
/// here too, and the cache cannot disagree with the editor that populated it.
/// </para>
/// </summary>
public sealed class MappingMetadataService(ConfigRepository configRepository, IColumnCatalog metadata)
{
    /// <summary>
    /// Refreshes both sides, saves, and reports what moved. Throws <see cref="FileNotFoundException"/>
    /// when the replication or the mapping is not there.
    /// <para>
    /// **A side that cannot be read leaves its cached columns alone rather than emptying them.** A
    /// source that is unreachable this minute is not a source with no columns, and overwriting a
    /// usable picture with an empty one because a connection was down would be the one outcome an
    /// operator pressing Refresh cannot want.
    /// </para>
    /// </summary>
    public async Task<MetadataRefreshResult> RefreshAsync(
        string replicationName, string mappingName, GitAuthor author, CancellationToken cancellationToken)
    {
        var task = configRepository.LoadReplicationTask(replicationName);
        var mapping = configRepository.LoadTableMapping(replicationName, mappingName);

        var source = mapping.Sources.Count == 1
            ? await ReadAsync(EndpointResolution.ResolveSource(task, mapping.Sources[0]), cancellationToken)
            : Unreadable("This mapping does not have exactly one source table to introspect.");

        var target = mapping.Targets.Count == 1
            ? await ReadAsync(EndpointResolution.ResolveTarget(task, mapping.Targets[0]), cancellationToken)
            : Unreadable("This mapping does not have exactly one target table to introspect.");

        var sourceSide = MetadataRefreshSide.Between(
            "source", mapping.SourceColumns, source.Columns, source.Unavailable);
        var targetSide = MetadataRefreshSide.Between(
            "target", mapping.TargetColumns, target.Columns, target.Unavailable);

        if (source.Columns is not null) mapping.SourceColumns = source.Columns;
        if (target.Columns is not null) mapping.TargetColumns = target.Columns;

        // Only when something was actually read. Stamping the moment a refresh failed on both sides
        // would date a picture nobody took.
        if (source.Columns is not null || target.Columns is not null)
            mapping.ColumnsCapturedUtc = DateTime.UtcNow;

        var saved = configRepository.SaveTableMapping(replicationName, mapping, author);
        return new MetadataRefreshResult(saved, sourceSide, targetSide);
    }

    /// <summary>
    /// A table that is not in the catalog is a normal state here, not a fault: the target of a
    /// mapping whose provisioning has yet to run genuinely has no columns, and the source of a
    /// query-configured reader has no catalog entry at all. Both come back as a stated reason.
    /// </summary>
    private async Task<SideRead> ReadAsync(TableRef table, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(table.Table))
            return Unreadable("This side names no table — a query source has no catalog to read.");

        try
        {
            var columns = await metadata.ListColumnsAsync(
                table.ConnectionName, table.Database, table.Schema, table.Table, cancellationToken);

            return new SideRead(
                columns.Select(c => new CachedColumn(c.Name, c.NativeType, c.IsNullable, c.IsPrimaryKey, c.IsIdentity))
                    .ToList(),
                null);
        }
        catch (InvalidOperationException ex)
        {
            return Unreadable(ex.Message);
        }
    }

    private static SideRead Unreadable(string reason) => new(null, reason);

    /// <param name="Columns">Null when the side could not be read at all — distinct from an empty
    /// list, which would be a table the catalog says has no columns.</param>
    private sealed record SideRead(List<CachedColumn>? Columns, string? Unavailable);
}

/// <summary>
/// What a refresh did to one side's cache. Named columns rather than counts, because "showing the
/// operator what changed" is the whole difference between this and a silent update: a refresh that
/// reports "3 changed" leaves them to go and find which three.
/// </summary>
/// <param name="Refreshed">False when the side could not be read; <paramref name="Unavailable"/> then
/// says why and the stored cache for that side is untouched.</param>
/// <param name="Changed">Columns present on both sides of the refresh whose type, nullability, key or
/// identity moved. The interesting case, and the one a count alone hides.</param>
public sealed record MetadataRefreshSide(
    string Side,
    bool Refreshed,
    string? Unavailable,
    int ColumnCount,
    IReadOnlyList<string> Added,
    IReadOnlyList<string> Removed,
    IReadOnlyList<string> Changed)
{
    public static MetadataRefreshSide Unreadable(string side, string reason) =>
        new(side, false, reason, 0, [], [], []);

    /// <summary>
    /// Compares the cache as it stood against what the catalog just said.
    /// <para>
    /// Paired by name, ordinally. A rename therefore reads as one removal and one addition rather
    /// than as a change, which is the honest answer: nothing in a catalog row says the new name is
    /// the old one, and guessing at it from a matching type would pair two unrelated columns as
    /// readily as two related ones.
    /// </para>
    /// <para>
    /// **A first capture reports every column as added, not as no change.** The cache went from
    /// saying nothing to saying five things, and that is what the operator is being shown.
    /// </para>
    /// </summary>
    public static MetadataRefreshSide Between(
        string side, IReadOnlyList<CachedColumn> before, List<CachedColumn>? columns, string? unavailable)
    {
        if (columns is null)
            return Unreadable(side, unavailable ?? "This side could not be read.");

        var was = before.ToDictionary(c => c.Name, StringComparer.Ordinal);
        var now = columns.ToDictionary(c => c.Name, StringComparer.Ordinal);

        return new MetadataRefreshSide(
            side,
            true,
            null,
            columns.Count,
            columns.Where(c => !was.ContainsKey(c.Name)).Select(c => c.Name).ToList(),
            before.Where(c => !now.ContainsKey(c.Name)).Select(c => c.Name).ToList(),
            columns.Where(c => was.TryGetValue(c.Name, out var old) && !c.SameShapeAs(old))
                .Select(c => c.Name).ToList());
    }
}

/// <param name="Mapping">The mapping as saved, so a client that just refreshed does not have to
/// re-fetch to see the cache it asked for.</param>
public sealed record MetadataRefreshResult(
    TableMappingConfig Mapping, MetadataRefreshSide Source, MetadataRefreshSide Target);
