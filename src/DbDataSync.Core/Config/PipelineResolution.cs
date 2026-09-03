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
    /// <paramref name="workItemKind"/> is the transient per-work-item override a Backfill already uses
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
}
