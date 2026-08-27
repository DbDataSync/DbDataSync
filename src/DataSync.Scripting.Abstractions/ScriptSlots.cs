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

    /// <summary>Transforms one column's value in this process, per cell, as rows flow from the reader
    /// to staging. Costs a delegate call per cell on the columns it declares — compare
    /// <see cref="SqlColumnExpression"/>, which the source evaluates and we pay nothing for.</summary>
    public const string ValueColumnExpression = "valueColumnExpression";

    /// <summary>Transforms a whole row in this process, and may drop it. The most powerful of the
    /// three and the most expensive.</summary>
    public const string RowTransform = "rowTransform";

    /// <summary>In the order they run: the source evaluates its SQL first, then values, then the whole
    /// row. Forced by where each one lives, and worth stating because a mapping using two of them on
    /// one column is otherwise guessing.</summary>
    public static IReadOnlyList<string> All { get; } = [SqlColumnExpression, ValueColumnExpression, RowTransform];

    public static bool IsKnown(string slot) => All.Contains(slot, StringComparer.Ordinal);
}
