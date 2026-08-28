using System.Text.Json.Serialization;
using YamlDotNet.Serialization;

namespace DataSync.Core.Config;

/// <summary>
/// What kind of value a parameter takes, and therefore what control an operator gets for it.
/// <para>
/// A closed set, deliberately. The point of declaring a parameter is that the SPA can render it
/// without knowing what it is *for*; an open-ended "type" string would put that knowledge back in the
/// SPA, which is what this replaces.
/// </para>
/// </summary>
public enum ParameterType
{
    /// <summary>The default, and what every parameter declared before this system existed reads back
    /// as — which is what makes adopting it additive rather than a migration.</summary>
    Text,
    Number,
    Bool,
    Date,
    DateTime,

    /// <summary>One of <see cref="ParameterDescriptor.DropdownOptions"/>.</summary>
    Dropdown,

    /// <summary>
    /// A column of the table mapping this parameter is being filled in for — resolved against the
    /// mapping's *column mappings* rather than against raw source or target columns, so an aliased
    /// column stays one selection rather than becoming two things to keep in step.
    /// </summary>
    ColumnPicker,

    /// <summary>A key and a value. A connection's free-form properties bag is a vararg of these,
    /// declared by the driver like anything else rather than assumed by the screen.</summary>
    Property,
}

/// <summary>
/// How many values a parameter takes. <c>1..1</c> is a single value, which is nearly everything;
/// anything else is a vararg and the form renders a list.
/// </summary>
public sealed record ParameterCardinality(int Min, int Max)
{
    [YamlIgnore, JsonIgnore]
    public static ParameterCardinality Single { get; } = new(1, 1);

    /// <summary>Zero or more, up to the guardrail — the shape a free-form bag takes.</summary>
    [YamlIgnore, JsonIgnore]
    public static ParameterCardinality Any { get; } = new(0, Ceiling);

    /// <summary>
    /// The ceiling on any one parameter's declared <see cref="Max"/>, independent of what it asks for.
    /// A declaration is authored by whoever wrote the driver or the script, and a form that tries to
    /// render ten thousand rows because a manifest said so is a browser that stops responding — this
    /// is the number that stops it, not a statement about what is reasonable.
    /// </summary>
    public const int Ceiling = 100;

    [YamlIgnore, JsonIgnore]
    public bool IsVararg => Min != 1 || Max != 1;
}

/// <summary>
/// Where a parameter goes on screen. Hints, not layout: the form decides how to honour them, and a
/// declaration that says nothing still renders.
/// </summary>
/// <param name="Card">Groups parameters into cards. Empty means the form's own default card.</param>
/// <param name="Group">Groups within a card — a row of related fields.</param>
/// <param name="Size">Relative width within its group, in flex units. 1 is the default.</param>
public sealed record ParameterLayout(string Card = "", string Group = "", double Size = 1)
{
    [YamlIgnore, JsonIgnore]
    public static ParameterLayout Default { get; } = new();
}

/// <summary>
/// One setting an author declares and an operator fills in.
/// <para>
/// The same relationship <c>DriverCapabilities</c> already has with the Kind pickers: the server says
/// what exists, the SPA renders it generically, and a new driver or script needs no SPA release to be
/// configurable. Three places solved this differently before — a hardcoded connection form, an
/// undeclared options bag, and a script manifest with a type that was only ever a string.
/// </para>
/// </summary>
/// <param name="Description">Shown beside the control. The place to say what a setting means, rather
/// than relying on its name to carry it.</param>
public sealed class ParameterDescriptor
{
    public required string Name { get; set; }

    /// <summary>What the operator reads. Defaults to <see cref="Name"/> when unset, because a
    /// declaration that has not thought about a label should still render a legible one.</summary>
    public string? Label { get; set; }

    public string? Description { get; set; }

    public ParameterType Type { get; set; } = ParameterType.Text;

    public bool Required { get; set; }

    /// <summary>
    /// Null — the common case — means a single value. Nullable rather than defaulted because config
    /// is git-committed and diffed in the UI: a non-null default would write
    /// <c>cardinality: {min: 1, max: 1}</c> into every manifest that never thought about it.
    /// Read it through <see cref="Occurrences"/>.
    /// </summary>
    public ParameterCardinality? Cardinality { get; set; }

    /// <summary>The choices, for <see cref="ParameterType.Dropdown"/>. Ignored otherwise.</summary>
    public List<string>? DropdownOptions { get; set; }

    /// <summary>What the form pre-fills. Null means empty — distinct from an empty string, which is a
    /// value somebody chose.</summary>
    public string? Default { get; set; }

    /// <summary>Null means the form's own defaults. Nullable for the same reason
    /// <see cref="Cardinality"/> is; read it through <see cref="Placement"/>.</summary>
    public ParameterLayout? Layout { get; set; }

    // Computed, and on neither wire. Serialized they would be written into every manifest — and then
    // fail to load, because a computed property has no setter to read them back into.

    [YamlIgnore, JsonIgnore]
    public string DisplayLabel => string.IsNullOrWhiteSpace(Label) ? Name : Label;

    /// <summary>How many values this takes, with the unstated case filled in.</summary>
    [YamlIgnore, JsonIgnore]
    public ParameterCardinality Occurrences => Cardinality ?? ParameterCardinality.Single;

    /// <summary>Where it goes, with the unstated case filled in.</summary>
    [YamlIgnore, JsonIgnore]
    public ParameterLayout Placement => Layout ?? ParameterLayout.Default;
}
