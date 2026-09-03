namespace DbDataSync.Core.Config;

/// <summary>
/// Which hooks run at a point, resolved mapping → replication → connection — the same rule and the
/// same reasons as <see cref="ScriptResolution"/>, including that the most specific level *replaces*
/// the list rather than appending to it: merging would mean reading a table mapping does not tell you
/// what runs.
/// <para>
/// The "connection" level is always the mapping's *target* connection: three of the four points
/// (<c>beforeStage</c>, <c>afterStage</c>, <c>beforeLoad</c>) are inherently about the staging/loading
/// side, and <c>afterLoad</c> is too even though a hook entry may itself opt into
/// <see cref="HookConnectionSide.Source"/>. A connection-level default for "the hooks every mapping
/// against this database needs" is a target-side concept, the same way <c>beforeLoad</c>/<c>afterLoad</c>
/// examples in the type's own doc all are.
/// </para>
/// </summary>
public static class HookResolution
{
    public static IReadOnlyList<HookConfig>? Resolve(
        string point,
        ConnectionConfig? targetConnection,
        ReplicationTaskConfig? task,
        TableMappingConfig? mapping) =>
        HierarchicalBinding.Resolve(point, mapping?.Hooks, task?.Hooks, targetConnection?.Hooks);

    public static BindingLevel LevelOf(
        string point,
        ConnectionConfig? targetConnection,
        ReplicationTaskConfig? task,
        TableMappingConfig? mapping) =>
        HierarchicalBinding.LevelOf(point, mapping?.Hooks, task?.Hooks, targetConnection?.Hooks);
}
