using System.Text;
using YamlDotNet.Serialization;

namespace DataSync.Core.Config;

/// <summary>
/// Reads and writes <c>&lt;RepoRoot&gt;/datasync.config.yaml</c> — phase 79's git-tracked,
/// human-editable home for the <c>DataSync:*</c> settings CONFIG.md documents (<c>Url</c>,
/// <c>StateEngine</c>, <c>StateConnectionString</c>, ...), the same shape a CLI flag or
/// <c>DataSync__Key</c> environment variable already sets.
/// <para>
/// Deliberately a sibling of <see cref="ConfigRepository"/>, not a method on it: this file lives at
/// the repo root, not under <c>config/</c>, and every command that needs it (<c>serve</c>,
/// <c>invite</c>, <c>health</c>) reads it before anything as heavy as a <see cref="ConfigRepository"/>
/// exists — <c>invite</c> and <c>health</c> never build one at all.
/// </para>
/// </summary>
public static class DataSyncConfigFile
{
    public const string FileName = "datasync.config.yaml";

    public static string PathIn(string repoRoot) => Path.Combine(repoRoot, FileName);

    /// <summary>
    /// The file's contents, flattened into the <c>Section:Key</c> shape ASP.NET Core's own
    /// configuration providers use — the same keys <c>--DataSync:Url</c> or <c>DataSync__Url</c>
    /// would set, so a caller reading either the file or the environment does it identically.
    /// <para>
    /// Empty (not missing) when the file doesn't exist or is entirely comments — every caller here
    /// already has its own "and then fall back to the default" behaviour for an unset key, so there is
    /// nothing this needs to distinguish that a caller would act on differently.
    /// </para>
    /// </summary>
    public static IReadOnlyDictionary<string, string?> Read(string repoRoot)
    {
        var path = PathIn(repoRoot);
        if (!File.Exists(path))
            return new Dictionary<string, string?>();

        var yaml = File.ReadAllText(path);
        if (string.IsNullOrWhiteSpace(yaml))
            return new Dictionary<string, string?>();

        var deserializer = new DeserializerBuilder().Build();
        var root = deserializer.Deserialize<Dictionary<object, object>?>(yaml);

        var flattened = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        if (root is not null)
            Flatten(root, prefix: null, flattened);

        return flattened;
    }

    private static void Flatten(object node, string? prefix, Dictionary<string, string?> into)
    {
        switch (node)
        {
            case IDictionary<object, object> map:
                foreach (var (key, value) in map)
                {
                    var segment = key?.ToString() ?? "";
                    var path = prefix is null ? segment : $"{prefix}:{segment}";
                    if (value is null)
                        into[path] = null;
                    else
                        Flatten(value, path, into);
                }
                break;

            // Array-shaped configuration (DataSync:Auth:Passkeys:Origins is the one example
            // elsewhere in this codebase) flattens to indexed keys the same way ASP.NET Core's own
            // JSON/environment providers do, on the chance a future DataSync:* setting is a list.
            case IList<object> list:
                for (var i = 0; i < list.Count; i++)
                    Flatten(list[i], prefix is null ? i.ToString() : $"{prefix}:{i}", into);
                break;

            default:
                into[prefix ?? ""] = node.ToString();
                break;
        }
    }

    /// <summary>
    /// Sets one <c>Section:Key</c> value, creating the file (and the section header) if either is
    /// missing, and leaving every other line — including comments — untouched.
    /// <para>
    /// **Not a round trip through YamlDotNet's serializer.** That was tried first: the object-mapping
    /// API YamlConfigSerializer already uses has no comment-preserving round trip (only the low-level
    /// parser/emitter event stream sees comments at all, and rebuilding a document from those events
    /// while injecting one new value is a lot of machinery for one file with a handful of keys). This
    /// instead edits the text directly — it finds the live (uncommented) line for <paramref
    /// name="key"/> under <paramref name="section"/> and replaces its value, or inserts a new line
    /// right after the section header if there isn't one yet. Every other line, comments included, is
    /// copied through unchanged. The tradeoff: this assumes the flat, two-level shape the starter file
    /// and every documented <c>DataSync:*</c> key actually use (<c>Section:</c> then two-space-indented
    /// <c>Key: value</c> lines) — it does not handle arbitrary YAML nesting on the write side the way
    /// <see cref="Read"/> does on the read side. That is sufficient for every key this phase or its
    /// successor (phase 81's admin screen) needs to write.
    /// </para>
    /// </summary>
    public static void SetValue(string repoRoot, string section, string key, string value)
    {
        // No credential is ever written into this file — StateConnectionString is the one key that
        // could carry one, and this is the one path anything writes it through.
        if (string.Equals(key, "StateConnectionString", StringComparison.OrdinalIgnoreCase))
            ConfigValidation.RejectEmbeddedCredential(value, $"{section}:{key}");

        var path = PathIn(repoRoot);
        var lines = File.Exists(path) ? File.ReadAllLines(path).ToList() : [];
        var valueText = QuoteYamlScalar(value);

        var sectionHeaderIndex = lines.FindIndex(l => l.TrimEnd() == $"{section}:");
        if (sectionHeaderIndex < 0)
        {
            if (lines.Count > 0 && lines[^1].Length > 0)
                lines.Add("");

            lines.Add($"{section}:");
            lines.Add($"  {key}: {valueText}");
            File.WriteAllLines(path, lines, Encoding.UTF8);
            return;
        }

        var keyLinePrefix = $"  {key}:";
        var sectionEnd = lines.Count;
        var keyLineIndex = -1;
        for (var i = sectionHeaderIndex + 1; i < lines.Count; i++)
        {
            var line = lines[i];
            // The section ends at the next non-indented, non-blank line (a new top-level section).
            if (line.Length > 0 && !char.IsWhiteSpace(line[0]) && !line.TrimStart().StartsWith('#'))
            {
                sectionEnd = i;
                break;
            }

            if (line.TrimStart().StartsWith(keyLinePrefix.TrimStart(), StringComparison.Ordinal)
                && !line.TrimStart().StartsWith('#'))
            {
                keyLineIndex = i;
                break;
            }
        }

        var newLine = $"{keyLinePrefix} {valueText}";
        if (keyLineIndex >= 0)
            lines[keyLineIndex] = newLine;
        else
            lines.Insert(sectionHeaderIndex + 1, newLine);

        File.WriteAllLines(path, lines, Encoding.UTF8);
    }

    /// <summary>
    /// Always double-quoted. A plain YAML scalar is legal unquoted for most of what goes in this file
    /// (a bare URL, an engine name), but a connection string's own <c>;</c>/<c>=</c> punctuation is one
    /// edge case away from being read back wrong, and one quoting rule that is always correct beats a
    /// "quote only when needed" rule that has to get every YAML corner case right.
    /// </summary>
    private static string QuoteYamlScalar(string value) =>
        $"\"{value.Replace("\\", "\\\\").Replace("\"", "\\\"")}\"";

    /// <summary>
    /// Written once, by <c>ServeCommand.Prepare</c>, only for a repo root that genuinely had no git
    /// repository before this run — never touching one that already exists. Every key is present but
    /// commented out: this is a reference for what can be set, not a default configuration silently
    /// taking effect.
    /// </summary>
    public static void WriteStarter(string repoRoot)
    {
        var path = PathIn(repoRoot);
        if (File.Exists(path))
            return;

        File.WriteAllText(path, StarterContent, Encoding.UTF8);
    }

    private const string StarterContent =
        """
        # DataSync:
        #   Url: http://localhost:5080
        #   StateEngine: MsSql
        #   # Connection string only — never a password. Set the password with:
        #   #   datasync secret set datasync:config:stateConnectionString "Password=..."
        #   StateConnectionString: "Server=sql01;Database=DataSyncState;"

        """;
}
