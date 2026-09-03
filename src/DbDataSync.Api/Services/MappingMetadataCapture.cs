using DbDataSync.Core.Config;

namespace DbDataSync.Api.Services;

/// <summary>
/// What an ordinary save is allowed to do to a mapping's cached column metadata — which is very
/// little, deliberately.
/// <para>
/// The editor captures the cache from the column lists it already fetched to draw its pickers, and it
/// only sends fresh ones when a side's table actually changed or nothing was cached yet. That rule
/// lives in the client because the client is what holds the fetch. **These two rules live here
/// because a client is not what should be trusted with them**, and every write path — the SPA, the
/// CLI, a curl — goes through the same <c>PUT</c>:
/// </para>
/// <list type="bullet">
/// <item>An empty incoming list never clears a populated cache. A client written before these fields
/// existed omits them, and a client whose catalog call failed this minute sends nothing; reading
/// either as "this table now has no columns" would destroy a good picture over an unrelated edit.</item>
/// <item><see cref="TableMappingConfig.ColumnsCapturedUtc"/> is the server's to stamp, and it moves
/// only when the stored columns actually change. A timestamp a client set would date the cache by
/// when somebody pressed Save rather than by when the catalog was last read.</item>
/// </list>
/// </summary>
public static class MappingMetadataCapture
{
    /// <summary>
    /// Reconciles the incoming mapping's cache against what is already on disk, in place.
    /// </summary>
    /// <param name="stored">The mapping as it stands, or null when this save creates it.</param>
    public static void Apply(TableMappingConfig incoming, TableMappingConfig? stored, DateTime now)
    {
        if (incoming.SourceColumns.Count == 0 && stored is not null)
            incoming.SourceColumns = stored.SourceColumns;
        if (incoming.TargetColumns.Count == 0 && stored is not null)
            incoming.TargetColumns = stored.TargetColumns;

        var unchanged = stored is not null
            && Same(incoming.SourceColumns, stored.SourceColumns)
            && Same(incoming.TargetColumns, stored.TargetColumns);

        if (unchanged)
        {
            incoming.ColumnsCapturedUtc = stored!.ColumnsCapturedUtc;
            return;
        }

        // Both lists empty and nothing stored is a mapping nobody has ever captured — which is a real
        // state (a pre-this-phase mapping, or one whose source is a query), and it is not a capture.
        incoming.ColumnsCapturedUtc =
            incoming.SourceColumns.Count == 0 && incoming.TargetColumns.Count == 0
                ? stored?.ColumnsCapturedUtc
                : now;
    }

    /// <summary>Order-sensitive, because a catalog's column order is part of what was captured — a
    /// table whose columns were reordered is a table whose picture changed.</summary>
    private static bool Same(List<CachedColumn> a, List<CachedColumn> b) =>
        a.Count == b.Count
        && a.Zip(b).All(pair =>
            string.Equals(pair.First.Name, pair.Second.Name, StringComparison.Ordinal)
            && pair.First.SameShapeAs(pair.Second));
}
