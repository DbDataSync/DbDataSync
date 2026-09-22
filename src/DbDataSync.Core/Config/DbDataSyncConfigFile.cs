using System.Text;
using YamlDotNet.Serialization;

namespace DbDataSync.Core.Config;

/// <summary>
/// Reads and writes <c>&lt;RepoRoot&gt;/dbdatasync.config.yaml</c> — phase 79's git-tracked,
/// human-editable home for the <c>DbDataSync:*</c> settings docs/configuration.md documents (<c>Url</c>,
/// <c>StateEngine</c>, <c>StateConnectionString</c>, ...), the same shape a CLI flag or
/// <c>DbDataSync__Key</c> environment variable already sets.
/// <para>
/// Deliberately a sibling of <see cref="ConfigRepository"/>, not a method on it: this file lives at
/// the repo root, not under <c>config/</c>, and every command that needs it (<c>serve</c>,
/// <c>invite</c>, <c>health</c>) reads it before anything as heavy as a <see cref="ConfigRepository"/>
/// exists — <c>invite</c> and <c>health</c> never build one at all.
/// </para>
/// </summary>
public static class DbDataSyncConfigFile
{
    public const string FileName = "dbdatasync.config.yaml";

    public static string PathIn(string repoRoot) => Path.Combine(repoRoot, FileName);

    /// <summary>
    /// The file's contents, flattened into the <c>Section:Key</c> shape ASP.NET Core's own
    /// configuration providers use — the same keys <c>--DbDataSync:App:Url</c> or <c>DbDataSync__App__Url</c>
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

            // Array-shaped configuration (DbDataSync:Auth:Passkeys:Origins is the one example
            // elsewhere in this codebase) flattens to indexed keys the same way ASP.NET Core's own
            // JSON/environment providers do, on the chance a future DbDataSync:* setting is a list.
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
    /// name="key"/> and replaces its value, or inserts a new line right after the section header if
    /// there isn't one yet. Every other line, comments included, is copied through unchanged.
    /// </para>
    /// <para>
    /// <paramref name="section"/> and <paramref name="key"/> say where to write a key the file does
    /// not have yet. A key it *does* have is updated where it already sits, whatever split between
    /// header and key line it was written with — the split is invisible to <see cref="Read"/>, and
    /// two writers in this codebase disagree about it: <c>setup</c> and phase 164's migration write
    /// <c>DbDataSync:Auth:Network:</c> then <c>Admin</c>, while the Admin config screen and
    /// <c>config set</c> write <c>DbDataSync:</c> then <c>Auth:Network:Admin</c>. Matching on the
    /// section header alone therefore missed a key that was plainly there, added a second line for
    /// it, and left the reader to choose between the two by document order — which is what made
    /// disabling <c>Auth:Network:Admin</c> from the Admin screen appear to do nothing at all.
    /// </para>
    /// </summary>
    public static void SetValue(string repoRoot, string section, string key, string value)
    {
        var fullKey = $"{section}:{key}";

        // No credential is ever written into this file — the state connection string is the one key
        // that could carry one, and this is the one path anything writes it through. Matched on the
        // whole flattened key rather than the bare `key` argument, because either half moving
        // (phase 164 renamed StateConnectionString to State:ConnectionString, and callers split it
        // at different points) silently leaves a bare-name check matching nothing at all.
        if (fullKey.EndsWith(":State:ConnectionString", StringComparison.OrdinalIgnoreCase)
            || fullKey.EndsWith(":StateConnectionString", StringComparison.OrdinalIgnoreCase))
            ConfigValidation.RejectEmbeddedCredential(value, fullKey);

        var path = PathIn(repoRoot);
        var lines = File.Exists(path) ? File.ReadAllLines(path).ToList() : [];
        var valueText = QuoteYamlScalar(value);

        // Normally one line; more than one only in a file an earlier build already wrote the
        // duplicate above into. All of them are set, rather than the extras pruned, so the file says
        // one thing however the reader resolves it — without deleting a line somebody may have
        // written a comment around.
        var existing = FindKeyLines(lines, fullKey);
        if (existing.Count > 0)
        {
            foreach (var (index, keyEnd) in existing)
                lines[index] = $"{lines[index][..keyEnd]} {valueText}";

            File.WriteAllLines(path, lines, Encoding.UTF8);
            return;
        }

        var sectionHeaderIndex = lines.FindIndex(l => l.TrimEnd() == $"{section}:");
        if (sectionHeaderIndex < 0)
        {
            if (lines.Count > 0 && lines[^1].Length > 0)
                lines.Add("");

            lines.Add($"{section}:");
            lines.Add($"  {key}: {valueText}");
        }
        else
        {
            lines.Insert(sectionHeaderIndex + 1, $"  {key}: {valueText}");
        }

        File.WriteAllLines(path, lines, Encoding.UTF8);
    }

    /// <summary>
    /// Every line that sets <paramref name="fullKey"/>, as the line's index and the offset just past
    /// the colon ending its key — enough to replace the value without touching the key text or its
    /// indentation, or to delete the line outright.
    /// <para>
    /// Keys are matched the way <see cref="Read"/> flattens them, so a key written as a nested
    /// mapping, as a colon-joined name under a shorter header, or as any mix of the two all match.
    /// This is the read side's model applied to the write side; the writer still only *creates* keys
    /// in the flat two-level shape, which is all any caller here asks for.
    /// </para>
    /// </summary>
    private static List<(int Index, int KeyEnd)> FindKeyLines(IReadOnlyList<string> lines, string fullKey)
    {
        var matches = new List<(int, int)>();
        var open = new List<(int Indent, string Path)>(); // the mappings this line is inside, outermost first

        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            var trimmed = line.TrimStart();
            // A blank line, a comment, or a sequence item — none of them name a key.
            if (trimmed.Length == 0 || trimmed.StartsWith('#') || trimmed.StartsWith('-'))
                continue;

            var colon = KeyColon(trimmed);
            if (colon < 0)
                continue;

            var indent = line.Length - trimmed.Length;
            while (open.Count > 0 && open[^1].Indent >= indent)
                open.RemoveAt(open.Count - 1);

            var name = trimmed[..colon];
            var path = open.Count == 0 ? name : $"{open[^1].Path}:{name}";

            // Nothing after the colon: a header, and everything more indented below it is inside it.
            if (trimmed[(colon + 1)..].Trim().Length == 0)
            {
                open.Add((indent, path));
                continue;
            }

            if (string.Equals(path, fullKey, StringComparison.OrdinalIgnoreCase))
                matches.Add((i, indent + colon + 1));
        }

        return matches;
    }

    /// <summary>
    /// Where the key ends on a <c>Key: value</c> line: the first colon that is followed by a space or
    /// ends the line. That is YAML's own rule, and the only one that reads both
    /// <c>Auth:Network:Admin: loopback</c> and <c>Url: http://localhost:5080</c> correctly — a colon
    /// inside a key is never followed by a space, and one inside a value is never reached. -1 for a
    /// line that names no key.
    /// </summary>
    private static int KeyColon(string trimmed)
    {
        for (var i = 0; i < trimmed.Length; i++)
        {
            if (trimmed[i] == ':' && (i == trimmed.Length - 1 || trimmed[i + 1] == ' '))
                return i;
        }

        return -1;
    }

    /// <summary>
    /// Removes one <c>Section:Key</c> line if present — <see cref="SetValue"/>'s counterpart, needed
    /// when a later write makes an earlier one meaningless rather than merely stale. Phase 113:
    /// switching <c>Kestrel:Certificates:Default</c> from a PEM cert+key pair to a PFX file must drop
    /// the old <c>KeyPath</c>, or Kestrel's own certificate loader still treats the section as
    /// PEM-shaped and tries to open the PFX file as a private key. A no-op when the key is not there.
    /// <para>
    /// Finds the key the same way <see cref="SetValue"/> does, for the same reason: the caller's
    /// split between section and key is not necessarily the one the file was written with, and a
    /// removal that quietly matches nothing leaves the superseded key behind still being read.
    /// </para>
    /// </summary>
    public static void RemoveValue(string repoRoot, string section, string key)
    {
        var path = PathIn(repoRoot);
        if (!File.Exists(path))
            return;

        var lines = File.ReadAllLines(path).ToList();
        var matches = FindKeyLines(lines, $"{section}:{key}");
        if (matches.Count == 0)
            return;

        // Back to front, so removing one line doesn't shift the index of the next.
        for (var i = matches.Count - 1; i >= 0; i--)
            lines.RemoveAt(matches[i].Index);

        File.WriteAllLines(path, lines, Encoding.UTF8);
    }

    /// <summary>
    /// As <see cref="SetValue"/>, for a YAML block sequence rather than a scalar — <c>Auth:Passkeys:Origins</c>
    /// (phase 110's <c>setup</c> command) is the one key documented so far that needs this shape; every
    /// other <c>DbDataSync:*</c> setting is a plain value. Replaces the whole existing block (the key
    /// line and every more-indented line under it), not just the key line, since a shorter new list
    /// left a stale trailing item behind otherwise.
    /// </summary>
    public static void SetListValue(string repoRoot, string section, string key, IReadOnlyList<string> values)
    {
        var path = PathIn(repoRoot);
        var lines = File.Exists(path) ? File.ReadAllLines(path).ToList() : [];
        var keyLinePrefix = $"  {key}:";
        var newBlock = new List<string> { keyLinePrefix };
        newBlock.AddRange(values.Select(v => $"    - {QuoteYamlScalar(v)}"));

        var sectionHeaderIndex = lines.FindIndex(l => l.TrimEnd() == $"{section}:");
        if (sectionHeaderIndex < 0)
        {
            if (lines.Count > 0 && lines[^1].Length > 0)
                lines.Add("");

            lines.Add($"{section}:");
            lines.AddRange(newBlock);
            File.WriteAllLines(path, lines, Encoding.UTF8);
            return;
        }

        var keyLineIndex = -1;
        var keyBlockEnd = -1;
        for (var i = sectionHeaderIndex + 1; i < lines.Count; i++)
        {
            var line = lines[i];
            if (line.Length > 0 && !char.IsWhiteSpace(line[0]) && !line.TrimStart().StartsWith('#'))
                break; // the next top-level section

            if (keyLineIndex < 0 && line.TrimStart().StartsWith(keyLinePrefix.TrimStart(), StringComparison.Ordinal)
                && !line.TrimStart().StartsWith('#'))
            {
                keyLineIndex = i;
                var keyIndent = line.Length - line.TrimStart().Length;
                var j = i + 1;
                while (j < lines.Count)
                {
                    var candidate = lines[j];
                    if (candidate.Length == 0)
                    {
                        j++;
                        continue;
                    }

                    var candidateIndent = candidate.Length - candidate.TrimStart().Length;
                    if (candidateIndent <= keyIndent)
                        break;

                    j++;
                }

                keyBlockEnd = j;
                break;
            }
        }

        if (keyLineIndex >= 0)
        {
            lines.RemoveRange(keyLineIndex, keyBlockEnd - keyLineIndex);
            lines.InsertRange(keyLineIndex, newBlock);
        }
        else
        {
            lines.InsertRange(sectionHeaderIndex + 1, newBlock);
        }

        File.WriteAllLines(path, lines, Encoding.UTF8);
    }

    /// <summary>
    /// <see cref="RemoveValue"/>'s counterpart for a <see cref="SetListValue"/>-shaped key — removing
    /// only the key's own header line (what <see cref="RemoveValue"/> does) would leave its
    /// <c>    - value</c> lines behind as an orphaned, malformed block. Finds the same key-line-plus-
    /// deeper-indented-lines extent <see cref="SetListValue"/> already computes and deletes the whole
    /// thing. A no-op when the section or key does not exist.
    /// </summary>
    public static void RemoveListValue(string repoRoot, string section, string key)
    {
        var path = PathIn(repoRoot);
        if (!File.Exists(path))
            return;

        var lines = File.ReadAllLines(path).ToList();
        var sectionHeaderIndex = lines.FindIndex(l => l.TrimEnd() == $"{section}:");
        if (sectionHeaderIndex < 0)
            return;

        var keyLinePrefix = $"  {key}:";
        for (var i = sectionHeaderIndex + 1; i < lines.Count; i++)
        {
            var line = lines[i];
            if (line.Length > 0 && !char.IsWhiteSpace(line[0]) && !line.TrimStart().StartsWith('#'))
                break; // the next top-level section

            if (!line.TrimStart().StartsWith(keyLinePrefix.TrimStart(), StringComparison.Ordinal)
                || line.TrimStart().StartsWith('#'))
                continue;

            var keyIndent = line.Length - line.TrimStart().Length;
            var j = i + 1;
            while (j < lines.Count)
            {
                var candidate = lines[j];
                if (candidate.Length == 0)
                {
                    j++;
                    continue;
                }

                var candidateIndent = candidate.Length - candidate.TrimStart().Length;
                if (candidateIndent <= keyIndent)
                    break;

                j++;
            }

            lines.RemoveRange(i, j - i);
            File.WriteAllLines(path, lines, Encoding.UTF8);
            return;
        }
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
        # DbDataSync:
        #   Url: http://localhost:5080
        #   StateEngine: MsSql
        #   # Connection string only — never a password. Set the password with:
        #   #   dbdatasync config secret set dbdatasync:config:stateConnectionString "Password=..."
        #   StateConnectionString: "Server=sql01;Database=DbDataSyncState;"

        """;
}
