using DataSync.Drivers.Abstractions;

namespace DataSync.Api.Models;

/// <summary>
/// A request to reload one table mapping's data, in whole or by segment. Engine-neutral: it names
/// reader/cache/writer Kinds (which a caller gets from
/// <c>GET /api/connections/{name}/capabilities</c>, never from a hardcoded list) and describes
/// segments structurally, with no SQL and no engine-specific terminology.
/// <para>
/// A backfill is a *run*, not a configuration change: this never reaches ConfigRepository and so never
/// produces a git commit, unlike everything the replication/mapping endpoints accept.
/// </para>
/// </summary>
public sealed class BackfillRequest
{
    /// <summary>Null means "use the replication's own configured Kind". In practice a backfill of an
    /// incrementally-synced replication must set at least <see cref="ReaderKind"/>, since an
    /// incremental reader reports changes since a watermark rather than reading a segment's rows.</summary>
    public string? ReaderKind { get; set; }

    public string? CacheKind { get; set; }

    public string? WriterKind { get; set; }

    /// <summary>
    /// An array from the outset even though the first SPA pass submits exactly one, so that
    /// multi-segment submission doesn't need a breaking change later — and so the shape matches the
    /// persisted <c>Reader.Options["segments"]</c> array a standalone reload replication uses.
    /// Each <c>Auto</c> entry is expanded server-side into concrete ranges before anything is
    /// enqueued, and every resulting segment becomes its own independently-scheduled unit of work.
    /// </summary>
    public List<BatchReloadSegment> Segments { get; set; } = [];
}
