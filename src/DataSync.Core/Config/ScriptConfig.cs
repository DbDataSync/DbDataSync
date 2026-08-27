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

/// <summary>Which language a script's code file is written in — see phase 26 §"Reusable hooks live in
/// the editor area". A SQL hook is the same artifact as a C# script, one field different: it has no
/// entry point to compile, and its code file sits beside the manifest as <c>.sql</c> instead of
/// <c>.cs</c>.</summary>
public enum ScriptLanguage
{
    CSharp,
    Sql,
}

/// <summary>
/// Persisted as config/scripts/&lt;name&gt;.yaml, beside config/scripts/&lt;name&gt;.cs (or, for a
/// <see cref="ScriptLanguage.Sql"/> hook, config/scripts/&lt;name&gt;.sql) which holds the code. Two
/// files rather than one because code embedded in YAML diffs badly, cannot be opened by an editor, and
/// re-indents on every round trip through a serializer.
/// </summary>
public sealed class ScriptConfig
{
    public required string Name { get; set; }

    /// <summary>Which extension point this implements — see <c>ScriptSlots</c> for a C# script, or
    /// <c>HookPoints</c>-shaped usage for a reusable SQL hook (bound at whichever point names it; the
    /// manifest itself does not fix one).</summary>
    public required string Kind { get; set; }

    public ScriptLanguage Language { get; set; } = ScriptLanguage.CSharp;

    /// <summary>The type in the script that implements the slot's contract. Required for
    /// <see cref="ScriptLanguage.CSharp"/>; meaningless — and left null — for
    /// <see cref="ScriptLanguage.Sql"/>, which has no entry point to compile.</summary>
    public string? EntryType { get; set; }

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
