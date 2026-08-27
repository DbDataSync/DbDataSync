namespace DataSync.Core.Config;

/// <summary>
/// A script bound to one slot: which script, and the parameters this binding supplies it.
/// <para>
/// The binding is **atomic**. The most specific level that sets a slot replaces both the name and the
/// parameters; they never merge across levels. Merging would mean that reading a table mapping does not
/// tell you what runs — you would have to read three files and combine them in your head, and the
/// config that meant "the replication's script with the connection's parameters" would look identical
/// to the config that got there by accident.
/// </para>
/// </summary>
public sealed class ScriptBinding
{
    public required string ScriptName { get; set; }
    public Dictionary<string, string> Parameters { get; set; } = new();
}

/// <summary>
/// Persisted as config/scripts/&lt;name&gt;.yaml, beside config/scripts/&lt;name&gt;.cs which holds the
/// code. Two files rather than one because code embedded in YAML diffs badly, cannot be opened by an
/// editor, and re-indents on every round trip through a serializer.
/// </summary>
public sealed class ScriptConfig
{
    public required string Name { get; set; }

    /// <summary>Which extension point this implements — see <c>ScriptSlots</c>.</summary>
    public required string Kind { get; set; }

    /// <summary>The type in the script that implements the slot's contract. Named explicitly rather
    /// than discovered, so a script holding helper types has an unambiguous entry point.</summary>
    public required string EntryType { get; set; }

    public string? Description { get; set; }

    public List<ScriptParameterDeclaration> Parameters { get; set; } = new();

    public bool Enabled { get; set; } = true;
}

public sealed class ScriptParameterDeclaration
{
    public required string Name { get; set; }
    public bool Required { get; set; }
    public string? Description { get; set; }
}

/// <summary>A script and its code, as one thing, for the API and the compiler.</summary>
public sealed class ScriptDefinition
{
    public required ScriptConfig Manifest { get; set; }
    public required string Code { get; set; }
}
