using DbDataSync.Core.Config;
using DbDataSync.Scripting.Abstractions;

namespace DbDataSync.Api.Services;

/// <summary>Where a script is bound: which level, which thing, and — for a slot binding — which slot.</summary>
/// <param name="Level">connection | replication | mapping, matching <c>BindingLevels</c>.</param>
/// <param name="Owner">The connection, replication, or "replication / mapping" that binds it.</param>
/// <param name="Slot">The script slot, or the hook point for a hook binding.</param>
public sealed record ScriptUsage(string Level, string Owner, string Slot);

/// <summary>
/// Answers the first question anyone has about a script they did not write: is this bound to anything?
/// <para>
/// A script bound nowhere is worth seeing as such — it is usually either a mistake or something safe
/// to delete, and neither is visible from a list of names and kinds.
/// </para>
/// <para>
/// Scans the config store rather than keeping an index. The store is small, git-backed and already
/// walked whole by <c>ListScripts</c>; an index would be a second source of truth that could disagree
/// with the config, which is the one thing worse than a scan.
/// </para>
/// </summary>
public sealed class ScriptUsageScanner(ConfigRepository configRepository)
{
    /// <summary>Every binding site in the store, keyed by script name. One pass, because the callers
    /// want the whole picture — the Scripts list shows a column, not one script's answer.</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<ScriptUsage>> ScanAll()
    {
        var usages = new Dictionary<string, List<ScriptUsage>>(StringComparer.Ordinal);

        void Record(string? scriptName, string level, string owner, string slot)
        {
            // A slot present with a null binding is "explicitly none", which is a real configuration
            // decision and not a use of any script — so there is nothing to attribute it to.
            if (string.IsNullOrEmpty(scriptName))
                return;
            if (!usages.TryGetValue(scriptName, out var list))
                usages[scriptName] = list = [];
            list.Add(new ScriptUsage(level, owner, slot));
        }

        void RecordAll(
            Dictionary<string, ScriptBinding?> scripts, Dictionary<string, List<HookConfig>?> hooks,
            string level, string owner)
        {
            foreach (var (slot, binding) in scripts)
                Record(binding?.ScriptName, level, owner, slot);

            // Hooks bind by name from a point's list rather than through the slot hierarchy (see
            // ScriptSlots.Hook), so they are a separate shape and would be missed by scanning Scripts
            // alone — which would report a reusable SQL hook in daily use as unused.
            foreach (var (point, list) in hooks)
            {
                foreach (var hook in list ?? [])
                    Record(hook.Hook, level, owner, point);
            }
        }

        foreach (var name in configRepository.ListConnections())
        {
            var connection = configRepository.LoadConnection(name);
            RecordAll(connection.Scripts, connection.Hooks, BindingLevels.Connection, name);
        }

        foreach (var replicationName in configRepository.ListReplications())
        {
            var task = configRepository.LoadReplicationTask(replicationName);
            RecordAll(task.Scripts, task.Hooks, BindingLevels.Replication, replicationName);

            foreach (var mappingName in configRepository.ListTableMappings(replicationName))
            {
                var mapping = configRepository.LoadTableMapping(replicationName, mappingName);
                RecordAll(mapping.Scripts, mapping.Hooks, BindingLevels.Mapping, $"{replicationName} / {mappingName}");
            }
        }

        return usages.ToDictionary(e => e.Key, e => (IReadOnlyList<ScriptUsage>)e.Value, StringComparer.Ordinal);
    }
}
