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

    /// <summary>A reusable SQL hook body's <c>Kind</c> — <c>Language = Sql</c>, no <c>EntryType</c>,
    /// bound by *name* from a hook point's list (<c>HookPoints</c> in <c>DataSync.Core.Config</c>),
    /// never from the hierarchy below. Deliberately **not** in <see cref="All"/>: that list also drives
    /// the SPA's generic per-slot <c>ScriptBindingsCard</c>, and a hook has no binding hierarchy of its
    /// own to show there — see phase 26. <c>ScriptsController</c> checks this one separately.</summary>
    public const string Hook = "hook";

    /// <summary>Answers what a connection's databases, tables and columns are, in place of the
    /// driver's catalog — see phase 29. <see cref="BindableAt"/> restricts it to the connection level:
    /// metadata describes an engine, not a mapping, and the SPA's pickers ask before any mapping
    /// exists.</summary>
    public const string MetadataProvider = "metadataProvider";

    /// <summary>C# that generates the SQL a lifecycle hook point runs — see phase 27. One slot for all
    /// four points: <see cref="ILifecycleHook.DeclarePoints"/> is how a binding says which ones it
    /// wants, not four separate bindings.</summary>
    public const string LifecycleHook = "lifecycleHook";

    /// <summary>In the order they run: the source evaluates its SQL first, then values, then the whole
    /// row. Forced by where each one lives, and worth stating because a mapping using two of them on
    /// one column is otherwise guessing. <see cref="Hook"/> is deliberately absent — see its own doc.</summary>
    public static IReadOnlyList<string> All { get; } =
        [SqlColumnExpression, ValueColumnExpression, RowTransform, LifecycleHook, MetadataProvider];

    /// <summary>
    /// Where a slot may be bound. Everything binds at all three levels except
    /// <see cref="MetadataProvider"/>, which binds only at the connection.
    /// <para>
    /// That is a real narrowing rather than an omission. Metadata describes an engine and a connection;
    /// a table's columns do not depend on which mapping is reading them, and the SPA's pickers ask
    /// before a mapping exists. A mapping-level binding would be invisible to the picker and visible
    /// only elsewhere, which is exactly the disagreement phase 29 exists to avoid.
    /// </para>
    /// <para>
    /// Exposed through the API's slot list so the SPA renders each slot only where it means something,
    /// rather than offering a control that silently does nothing.
    /// </para>
    /// </summary>
    public static IReadOnlyList<string> BindableAt(string level) =>
        level == BindingLevels.Connection
            ? All
            : All.Where(s => s != MetadataProvider).ToList();

    public static bool IsBindableAt(string slot, string level) => BindableAt(level).Contains(slot, StringComparer.Ordinal);

    /// <summary>
    /// What a slot is called, and what binding one does, in words an operator can act on.
    /// <para>
    /// Served through the API rather than kept in the SPA because the slot list is the server's: a
    /// build that adds a slot should not need a matching SPA release before the new slot has a name.
    /// <c>sqlColumnExpression</c> is a good key and a bad label.
    /// </para>
    /// </summary>
    public static (string Label, string Description) Describe(string slot) => slot switch
    {
        SqlColumnExpression => (
            "Source SQL for a column",
            "Generates a SQL expression the source database evaluates, in place of a literal transform. Costs nothing here — the source does the work."),
        ValueColumnExpression => (
            "Value transform",
            "Rewrites one column's value in this process, cell by cell, between the reader and staging."),
        RowTransform => (
            "Row transform",
            "Rewrites or drops whole rows in this process. The most capable of the three, and the most expensive."),
        LifecycleHook => (
            "Generated lifecycle SQL",
            "Generates the SQL run before or after a stage, in place of writing it by hand."),
        MetadataProvider => (
            "Catalog provider",
            "Answers what this connection's databases, tables and columns are, in place of the driver's own catalog."),
        Hook => (
            "Lifecycle hook body",
            "Reusable SQL for a lifecycle hook point, selected by name rather than through the binding hierarchy."),
        _ => (slot, ""),
    };

    /// <summary>Every <c>Kind</c> a script's manifest may declare — the four bindable slots in
    /// <see cref="All"/> plus <see cref="Hook"/>, which is known but not one of them.</summary>
    public static bool IsKnown(string slot) => slot == Hook || All.Contains(slot, StringComparer.Ordinal);
}
