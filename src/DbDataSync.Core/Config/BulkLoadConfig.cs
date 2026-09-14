namespace DbDataSync.Core.Config;

/// <summary>
/// The Bulk Load pipeline — a second pipeline beside <see cref="ChangeProcessingConfig"/>, used for an
/// on-demand reload (what <c>BulkLoadService</c> queues) and, from phase 134, for an initial load.
/// <para>
/// <see cref="Reader"/> defaults to the <c>BatchReload</c> Kind every driver is expected to offer for a
/// whole-table read — a replication that says nothing here still gets a working Bulk Load pipeline.
/// <see cref="Cache"/> and <see cref="Writer"/> are null by default and, when null, inherit the
/// replication's <see cref="ChangeProcessingConfig.Cache"/>/<see cref="ChangeProcessingConfig.Writer"/>
/// (see <see cref="PipelineResolution.BulkLoadCache"/>/<see cref="PipelineResolution.BulkLoadWriter"/>)
/// — a bulk (re)load into the same target table naturally uses the same staging and write mechanism as
/// change processing unless an operator wants something different (typically an upsert-only writer for
/// a first load; see the writer capability note below).
/// </para>
/// <para>
/// Segmenting is deliberately not part of this — it stays on <see cref="TableMappingConfig.DefaultSegmenting"/>,
/// because how a table divides is a fact about the table, not about a pipeline.
/// </para>
/// <para>
/// The writer here is warned about, not constrained, when it does not support reconciliation:
/// reconciliation converges a *drifted* target, but a first load into a table that was just created has
/// nothing to remove, so an upsert-only writer is correct and cheaper there. The UI surfaces
/// <c>WriterCapability.SupportsReconciliation</c> exactly as the Bulk Load / former Backfill form does.
/// </para>
/// </summary>
public sealed class BulkLoadConfig
{
    public ReaderConfig Reader { get; set; } = new() { Kind = "BatchReload" };
    public CacheConfig? Cache { get; set; }
    public WriterConfig? Writer { get; set; }
}
