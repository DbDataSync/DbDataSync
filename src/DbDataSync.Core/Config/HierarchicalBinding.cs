namespace DbDataSync.Core.Config;

/// <summary>
/// The three-level "most specific replaces, absent inherits, present-with-null overrides to none"
/// walk shared by <see cref="ScriptResolution"/> and <see cref="HookResolution"/> — extracted so the
/// inheritance rule has exactly one implementation. Two copies of an inheritance rule is how they
/// drift: a fix applied to one and not the other becomes a bug that only shows up for hooks (or only
/// for scripts), months later.
/// </summary>
public static class HierarchicalBinding
{
    public static TValue? Resolve<TValue>(
        string key,
        Dictionary<string, TValue?>? mappingBindings,
        Dictionary<string, TValue?>? taskBindings,
        Dictionary<string, TValue?>? connectionBindings)
        where TValue : class
    {
        if (mappingBindings is not null && mappingBindings.TryGetValue(key, out var fromMapping))
            return fromMapping;
        if (taskBindings is not null && taskBindings.TryGetValue(key, out var fromTask))
            return fromTask;
        if (connectionBindings is not null && connectionBindings.TryGetValue(key, out var fromConnection))
            return fromConnection;
        return null;
    }

    /// <summary>Where a resolved binding came from, for a UI that wants to show INHERITED.</summary>
    public static BindingLevel LevelOf<TValue>(
        string key,
        Dictionary<string, TValue?>? mappingBindings,
        Dictionary<string, TValue?>? taskBindings,
        Dictionary<string, TValue?>? connectionBindings)
    {
        if (mappingBindings?.ContainsKey(key) == true) return BindingLevel.Mapping;
        if (taskBindings?.ContainsKey(key) == true) return BindingLevel.Replication;
        if (connectionBindings?.ContainsKey(key) == true) return BindingLevel.Connection;
        return BindingLevel.Unbound;
    }
}

public enum BindingLevel
{
    Unbound,
    Connection,
    Replication,
    Mapping,
}
