namespace DbDataSync.Core.Config;

/// <summary>The four points around one unit of work's staging and loading — see phase 26. A list per
/// point runs in declared order; the config key names never renumber, the same reason
/// <c>ScriptSlots</c> gives.</summary>
public static class HookPoints
{
    public const string BeforeStage = "beforeStage";
    public const string AfterStage = "afterStage";
    public const string BeforeLoad = "beforeLoad";
    public const string AfterLoad = "afterLoad";

    public static IReadOnlyList<string> All { get; } = [BeforeStage, AfterStage, BeforeLoad, AfterLoad];

    public static bool IsKnown(string point) => All.Contains(point, StringComparer.Ordinal);
}

public enum HookConnectionSide
{
    Target,
    Source,
}

public enum HookErrorMode
{
    Fail,
    Warn,
}

/// <summary>
/// One entry in a hook point's list — either inline SQL (a one-off, e.g. a single <c>UPDATE STATISTICS</c>)
/// or a reference to a reusable named hook authored in the Scripts area (<see cref="Hook"/> non-null).
/// Never both; <see cref="ConfigValidationException"/> at save time names which. Inline and named
/// entries are otherwise interchangeable and may sit in the same list, in declared order.
/// </summary>
public sealed class HookConfig
{
    /// <summary>A label for logging and the UI — not an identity. Defaults to <see cref="Hook"/> when
    /// that is set and this is not.</summary>
    public string? Name { get; set; }

    /// <summary>Inline SQL, in the same authoring model as <c>ColumnMapping.Transform</c> and
    /// <c>SourceTableSpec.Filter</c>: admin-authored, not parameterizable free text.</summary>
    public string? Sql { get; set; }

    /// <summary>The name of a reusable SQL hook script (<c>config/scripts/&lt;name&gt;.sql</c> +
    /// <c>.yaml</c>, <see cref="ScriptConfig.Language"/> <see cref="ScriptLanguage.Sql"/>).</summary>
    public string? Hook { get; set; }

    /// <summary>Values for the named hook's declared parameters — substituted as identifiers, the same
    /// quoting rule as every built-in token. Only meaningful with <see cref="Hook"/>.</summary>
    public Dictionary<string, string> Parameters { get; set; } = new();

    /// <summary>Which connection this hook runs on. Target by default — see phase 26's "Which
    /// connection, and why the two sides differ".</summary>
    public HookConnectionSide Connection { get; set; } = HookConnectionSide.Target;

    /// <summary>Fail the run (default) or log at Warning and continue.</summary>
    public HookErrorMode OnError { get; set; } = HookErrorMode.Fail;
}
