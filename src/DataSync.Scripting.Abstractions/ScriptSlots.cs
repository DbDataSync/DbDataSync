namespace DataSync.Scripting.Abstractions;

/// <summary>
/// The names a script binding is keyed by, in config and in the resolution hierarchy. Constants rather
/// than an enum because they are persisted in YAML and appear in URLs: a slot added later must not
/// renumber the ones already written to disk.
/// </summary>
public static class ScriptSlots
{
    /// <summary>Generates a SQL expression in the *source's* dialect for one column, evaluated by the
    /// source engine. The generated string goes where a literal <c>ColumnMapping.Transform</c> would —
    /// see phase 22.</summary>
    public const string SqlColumnExpression = "sqlColumnExpression";

    public static IReadOnlyList<string> All { get; } = [SqlColumnExpression];

    public static bool IsKnown(string slot) => All.Contains(slot, StringComparer.Ordinal);
}
