using DbDataSync.Core.Config;

namespace DbDataSync.Drivers.Abstractions;

/// <summary>
/// A reader, writer or staging provider needed a column's cached shape and the mapping's cache
/// (<see cref="TableMappingConfig.SourceColumns"/>/<see cref="TableMappingConfig.TargetColumns"/>,
/// phase 90) didn't have it — either because nothing has ever been captured, or because the specific
/// column asked for isn't in what was captured.
/// <para>
/// Mirrors <see cref="PositionExpiredException"/>'s shape: a specific type rather than a generic
/// message, because the **fix is distinct** in exactly the same way. A generic failure means "look at
/// the logs"; this one means "press Refresh metadata on this mapping", which is a specific action an
/// operator can take rather than a mystery to debug. See
/// architecture/planning/done/reader-writer-metadata-cache-consumption.md's "design that was rejected"
/// — the alternative was a live fallback that would have hidden this distinction entirely.
/// </para>
/// <para>
/// **Deliberately the only outcome of a missing or incomplete cache.** Phase 91 reads
/// <c>SourceColumns</c>/<c>TargetColumns</c> and nothing else at run time — there is no live
/// <c>ITableCatalog</c>/<c>IDriver</c> call left to fall back to, so "the column isn't in the cache" and
/// "the column doesn't exist at all" are indistinguishable from here, and deliberately so. Refreshing
/// tells the two apart; this exception's job is only to say which mapping and which side need it.
/// </para>
/// </summary>
/// <param name="mappingName">The table mapping whose cache was needed, as an operator named it.</param>
/// <param name="side">"source" or "target" — which cache was consulted.</param>
/// <param name="column">The specific column that wasn't found, or null when the whole cache is empty
/// and there was nothing to look a name up in.</param>
public sealed class MetadataNotCachedException(string mappingName, string side, string? column = null)
    : Exception(BuildMessage(mappingName, side, column))
{
    public string MappingName { get; } = mappingName;
    public string Side { get; } = side;
    public string? Column { get; } = column;

    private static string BuildMessage(string mappingName, string side, string? column) =>
        column is null
            ? $"Table mapping '{mappingName}' has no cached {side} column metadata. This mapping's " +
              $"{side} shape has never been captured, or a save cleared it. Use Refresh metadata on " +
              "the mapping to populate it before this run can proceed."
            : $"Table mapping '{mappingName}' has no cached {side} column '{column}'. The mapping's " +
              $"cached {side} shape doesn't include it — the table may have changed since it was last " +
              "captured. Use Refresh metadata on the mapping to populate it before this run can proceed.";
}

/// <summary>
/// Looks a column up in a mapping's cached shape, throwing <see cref="MetadataNotCachedException"/>
/// rather than returning null — every one of phase 91's six consumers needs the column it asks for or
/// it cannot run at all, so there is no caller that wants an optional answer here to check itself.
/// <para>
/// Lives beside the exception it throws rather than in <c>DbDataSync.Core.Config</c> (where
/// <see cref="CachedColumn"/> itself lives) because the reverse reference already exists —
/// <c>DbDataSync.Drivers.Abstractions</c> references <c>DbDataSync.Core</c>, not the other way round (see
/// <see cref="CachedColumn"/>'s own doc comment) — and because converting to <see cref="ColumnMetadata"/>
/// is exactly the shape every consumer here already works in.
/// </para>
/// </summary>
public static class CachedMetadataLookup
{
    /// <summary>One column, by name, ordinal-insensitively (the same comparison every live
    /// <c>ITableCatalog</c> lookup this replaces used). Throws naming the mapping, the side, and the
    /// column when the cache is empty or the column isn't in it — never returns null.</summary>
    public static ColumnMetadata RequireColumn(
        this IReadOnlyList<CachedColumn> cached, string mappingName, string side, string columnName)
    {
        if (cached.Count == 0)
            throw new MetadataNotCachedException(mappingName, side);

        var found = cached.FirstOrDefault(c => string.Equals(c.Name, columnName, StringComparison.OrdinalIgnoreCase));
        return found is null
            ? throw new MetadataNotCachedException(mappingName, side, columnName)
            : found.ToColumnMetadata();
    }

    /// <summary>The whole cached list, converted — for a consumer that needs more than one named column
    /// (the key/non-key split, a target's full shape). Throws when the cache is empty; an empty list is
    /// never a valid "no columns" answer here the way it legitimately is to the API's own metadata
    /// refresh, because a table with zero columns cannot be replicated in the first place.</summary>
    public static IReadOnlyList<ColumnMetadata> RequireAll(
        this IReadOnlyList<CachedColumn> cached, string mappingName, string side)
    {
        if (cached.Count == 0)
            throw new MetadataNotCachedException(mappingName, side);

        return cached.Select(ToColumnMetadata).ToList();
    }

    public static ColumnMetadata ToColumnMetadata(this CachedColumn column) =>
        new(column.Name, column.NativeType, column.IsNullable, column.IsPrimaryKey, column.IsIdentity);
}
