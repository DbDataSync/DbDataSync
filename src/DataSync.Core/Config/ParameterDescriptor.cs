using System.ComponentModel;
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

    /// <summary>
    /// A credential. Rendered masked, and — the part that matters — **never sent back to the client**:
    /// a form loads with it blank, and blank on save means "keep what is stored". The connection
    /// password has worked that way since phase 3; naming it as a type is what stops the next secret
    /// parameter reinventing the contract, or forgetting it.
    /// </summary>
    Secret,
}

/// <summary>
/// How many values a parameter takes. <c>1..1</c> is a single value, which is nearly everything;
/// anything else is a vararg and the form renders a list.
/// <para>
/// A <c>record</c> for its value equality, but with a parameterless constructor and settable
/// properties rather than a positional one: the YAML deserializer constructs through exactly those,
/// and a positional record serializes out to disk perfectly well and then throws on the way back in.
/// The bug that shape caused was invisible until a script declaring a vararg parameter was saved and
/// then reopened, which no test had done.
/// </para>
/// </summary>
public sealed record ParameterCardinality
{
    public ParameterCardinality() { }

    public ParameterCardinality(int min, int max)
    {
        Min = min;
        Max = max;
    }

    /// <summary>
    /// <see cref="DefaultValueAttribute"/> is load-bearing here, exactly as it is on
    /// <c>ReplicationTaskConfig.Enabled</c>: the serializer omits defaults by comparing against
    /// <c>default(T)</c>, so a <c>Min</c> of <c>0</c> — the thing that makes a parameter optional —
    /// was omitted and the initializer put <c>1</c> back on load. Comparing against <c>1</c> instead
    /// writes the interesting value and omits the boring one, which is the right way round.
    /// </summary>
    [DefaultValue(1)]
    public int Min { get; set; } = 1;

    [DefaultValue(1)]
    public int Max { get; set; } = 1;

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
/// <remarks>Same shape as <see cref="ParameterCardinality"/>, for the same round-trip reason.</remarks>
public sealed record ParameterLayout
{
    public ParameterLayout() { }

    public ParameterLayout(string card = "", string group = "", double size = 1)
    {
        Card = card;
        Group = group;
        Size = size;
    }

    /// <summary>Groups parameters into cards. Empty means the form's own default card.</summary>
    [DefaultValue("")]
    public string Card { get; set; } = "";

    /// <summary>Groups within a card — a row of related fields.</summary>
    [DefaultValue("")]
    public string Group { get; set; } = "";

    /// <summary>Relative width within its group, in flex units. 1 is the default.</summary>
    [DefaultValue(1d)]
    public double Size { get; set; } = 1;

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

    /// <summary>
    /// How to write each option for a person, keyed by the value that gets stored. Optional, and
    /// absent for the many cases where the stored value already reads as itself.
    /// <para>
    /// A map rather than a parallel list of labels, because a parallel list is one edit away from
    /// being off by one and labelling the wrong option — a mistake that looks like working software.
    /// The values in <see cref="DropdownOptions"/> stay the single source of truth for what is
    /// allowed; this only changes how they are spelled on screen.
    /// </para>
    /// </summary>
    public Dictionary<string, string>? DropdownLabels { get; set; }

    /// <summary>What the form pre-fills. Null means empty — distinct from an empty string, which is a
    /// value somebody chose.</summary>
    public string? Default { get; set; }

    /// <summary>Null means the form's own defaults. Nullable for the same reason
    /// <see cref="Cardinality"/> is; read it through <see cref="Placement"/>.</summary>
    public ParameterLayout? Layout { get; set; }

    /// <summary>
    /// Whether this parameter applies at all, given the other values. Computed by whoever declares it
    /// — a driver deciding that Host is beside the point once the operator picked connection-string
    /// addressing — and the form simply renders what it is told.
    /// <para>
    /// Server-side rather than a condition expression the client evaluates, because the rule belongs
    /// to the thing that owns the setting. A client-side condition language would be a second place
    /// that has to agree about what "SqlAuth" implies, and it would be the place that is wrong.
    /// </para>
    /// <para>
    /// <see cref="DefaultValueAttribute"/> is load-bearing: the serializer omits values equal to
    /// <c>default(T)</c>, so without it a deliberate <c>false</c> is the one thing that never gets
    /// written down. Same trap as <c>ReplicationTaskConfig.Enabled</c>.
    /// </para>
    /// </summary>
    [DefaultValue(true)]
    public bool Visible { get; set; } = true;

    /// <summary>
    /// Whether changing this value changes the answer to "what does this thing take?" — and therefore
    /// whether the client has to ask again.
    /// <para>
    /// Declared rather than inferred, so a form refetches on the two dropdowns that matter instead of
    /// on every keystroke in a host field. A parameter nothing depends on says nothing and costs
    /// nothing.
    /// </para>
    /// </summary>
    public bool Recalc { get; set; }

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
