namespace DbDataSync.Core.Config;

/// <summary>
/// Which reader, staging provider and writer a table mapping actually runs — the replication's, unless
/// the mapping states its own.
/// <para>
/// Two levels and exactly three fixed keys, each independently overridable, so a <c>??</c> per stage is
/// the whole rule. Deliberately **not** <c>HierarchicalBinding</c>: that walks a dictionary keyed by
/// slot across three levels (connection → replication → mapping), and there is no connection-level
/// pipeline config to walk. Adapting it here would be more machinery than the question has.
/// </para>
/// <para>
/// An override replaces its stage whole — Kind and Options together. See
/// <see cref="TableMappingConfig.ReaderOverride"/> for why that is atomic rather than merged.
/// </para>
/// </summary>
public static class PipelineResolution
{
    public static ReaderConfig Reader(ReplicationTaskConfig task, TableMappingConfig? mapping) =>
        mapping?.ReaderOverride ?? task.ChangeProcessing.Reader;

    public static CacheConfig Cache(ReplicationTaskConfig task, TableMappingConfig? mapping) =>
        mapping?.CacheOverride ?? task.ChangeProcessing.Cache;

    public static WriterConfig Writer(ReplicationTaskConfig task, TableMappingConfig? mapping) =>
        mapping?.WriterOverride ?? task.ChangeProcessing.Writer;

    /// <summary>
    /// The Kind a pass actually runs, most specific first.
    /// <para>
    /// <paramref name="workItemKind"/> is the transient per-work-item override a BulkLoad already uses
    /// — it stays the most specific, applied on top of the resolved stage exactly as it was applied on
    /// top of the replication's before mappings could override anything.
    /// </para>
    /// </summary>
    public static string ReaderKind(string? workItemKind, ReplicationTaskConfig task, TableMappingConfig? mapping) =>
        workItemKind ?? Reader(task, mapping).Kind;

    public static string CacheKind(string? workItemKind, ReplicationTaskConfig task, TableMappingConfig? mapping) =>
        workItemKind ?? Cache(task, mapping).Kind;

    public static string WriterKind(string? workItemKind, ReplicationTaskConfig task, TableMappingConfig? mapping) =>
        workItemKind ?? Writer(task, mapping).Kind;

    /// <summary>Where a resolved stage came from, for a UI that wants to show INHERITED.</summary>
    public static BindingLevel LevelOfReader(TableMappingConfig? mapping) =>
        mapping?.ReaderOverride is null ? BindingLevel.Replication : BindingLevel.Mapping;

    public static BindingLevel LevelOfCache(TableMappingConfig? mapping) =>
        mapping?.CacheOverride is null ? BindingLevel.Replication : BindingLevel.Mapping;

    public static BindingLevel LevelOfWriter(TableMappingConfig? mapping) =>
        mapping?.WriterOverride is null ? BindingLevel.Replication : BindingLevel.Mapping;

    public static ReaderConfig BulkLoadReader(ReplicationTaskConfig task, TableMappingConfig? mapping) =>
        mapping?.BulkLoadReaderOverride ?? task.BulkLoad.Reader;

    /// <summary>Null at both the mapping override and the replication's <see cref="BulkLoadConfig.Cache"/>
    /// falls through to the fully-resolved Change Processing cache — see <see cref="BulkLoadConfig"/>'s
    /// own doc comment for why that is the right default.</summary>
    public static CacheConfig BulkLoadCache(ReplicationTaskConfig task, TableMappingConfig? mapping) =>
        mapping?.BulkLoadCacheOverride ?? task.BulkLoad.Cache ?? Cache(task, mapping);

    public static WriterConfig BulkLoadWriter(ReplicationTaskConfig task, TableMappingConfig? mapping) =>
        mapping?.BulkLoadWriterOverride ?? task.BulkLoad.Writer ?? Writer(task, mapping);

    public static string BulkLoadReaderKind(string? workItemKind, ReplicationTaskConfig task, TableMappingConfig? mapping) =>
        workItemKind ?? BulkLoadReader(task, mapping).Kind;

    public static string BulkLoadCacheKind(string? workItemKind, ReplicationTaskConfig task, TableMappingConfig? mapping) =>
        workItemKind ?? BulkLoadCache(task, mapping).Kind;

    public static string BulkLoadWriterKind(string? workItemKind, ReplicationTaskConfig task, TableMappingConfig? mapping) =>
        workItemKind ?? BulkLoadWriter(task, mapping).Kind;

    /// <summary>Where a resolved Bulk Load stage came from, for a UI that wants to show INHERITED. Only
    /// reports mapping-vs-replication, the same granularity <see cref="LevelOfReader"/> does — it does not
    /// distinguish "replication's own BulkLoad.Cache" from "fell through to ChangeProcessing.Cache", which
    /// is an internal resolution detail, not something an operator overrides differently.</summary>
    public static BindingLevel LevelOfBulkLoadReader(TableMappingConfig? mapping) =>
        mapping?.BulkLoadReaderOverride is null ? BindingLevel.Replication : BindingLevel.Mapping;

    public static BindingLevel LevelOfBulkLoadCache(TableMappingConfig? mapping) =>
        mapping?.BulkLoadCacheOverride is null ? BindingLevel.Replication : BindingLevel.Mapping;

    public static BindingLevel LevelOfBulkLoadWriter(TableMappingConfig? mapping) =>
        mapping?.BulkLoadWriterOverride is null ? BindingLevel.Replication : BindingLevel.Mapping;

    /// <summary>The resolved <see cref="ReconcileConfig"/> itself — phase 125's two-level override,
    /// same rule as <see cref="Reader"/>/<see cref="Cache"/>/<see cref="Writer"/> above.</summary>
    public static ReconcileConfig Reconcile(ReplicationTaskConfig task, TableMappingConfig? mapping) =>
        mapping?.ReconcileOverride ?? task.Reconcile;

    /// <summary>"KeyReconcile" unless the resolved <see cref="ReconcileConfig.Reader"/> names something
    /// else — null there means the only real answer, not "inherit", since there is nothing above a
    /// mapping's own <see cref="ReconcileConfig"/> to inherit from.</summary>
    public static string ReconcileReaderKind(ReplicationTaskConfig task, TableMappingConfig? mapping) =>
        Reconcile(task, mapping).Reader?.Kind ?? "KeyReconcile";

    public static string ReconcileCacheKind(ReplicationTaskConfig task, TableMappingConfig? mapping) =>
        Reconcile(task, mapping).Cache?.Kind ?? "StagingTable";

    /// <summary>"KeyReconcileDelete" unless the resolved <see cref="ReconcileConfig.Writer"/> names
    /// something else, except when the mapping's own primary writer is <c>Scd2</c> — a delete-diff
    /// sweep against a versioned target has to close a version rather than remove the row, so the
    /// unset default there is <c>KeyReconcileScd2Close</c> instead (phase 129). An explicit
    /// <see cref="ReconcileConfig.Writer"/> override still wins outright; this only changes what
    /// "unset" resolves to.</summary>
    public static string ReconcileWriterKind(ReplicationTaskConfig task, TableMappingConfig? mapping) =>
        Reconcile(task, mapping).Writer?.Kind
        ?? (Writer(task, mapping).Kind == "Scd2" ? "KeyReconcileScd2Close" : "KeyReconcileDelete");

    public static BindingLevel LevelOfReconcile(TableMappingConfig? mapping) =>
        mapping?.ReconcileOverride is null ? BindingLevel.Replication : BindingLevel.Mapping;
}
