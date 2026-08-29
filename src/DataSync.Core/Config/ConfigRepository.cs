using ClrKernel.Core.Secrets;
using DataSync.Core.Git;
using DataSync.Core.Secrets;

namespace DataSync.Core.Config;

/// <summary>
/// The single read/write path for replication config: validates, serializes to YAML, writes to disk
/// under <paramref name="configRoot"/>, auto-commits via <see cref="GitCommitService"/>, and — for
/// connections — resolves credentials through <see cref="SecretStore"/> so a plaintext password is
/// never the thing that gets serialized. See architecture/detailed-design.md §3.6.
/// </summary>
public sealed class ConfigRepository
{
    private readonly string _configRoot;
    private readonly GitCommitService _git;
    private readonly SecretStore _secrets;

    public ConfigRepository(string configRoot, GitCommitService git, SecretStore secrets)
    {
        _configRoot = configRoot;
        _git = git;
        _secrets = secrets;

        Directory.CreateDirectory(ConfigPaths.ConnectionsDir(_configRoot));
        Directory.CreateDirectory(ConfigPaths.ReplicationsDir(_configRoot));
    }

    // ---- Connections ----

    public ConnectionConfig SaveConnection(ConnectionInput input, GitAuthor author)
    {
        ConfigValidation.ValidateName(input.Name, nameof(input.Name));
        // Before anything with a side effect. This used to sit after the credential block, which meant
        // a save rejected for having a password in its connection string had already written that
        // password to the secret store on the way to being rejected.
        ConfigValidation.ValidateAddressing(input.Host, input.ConnectionString, input.Name);

        string? secretRef = null;
        if (input.AuthMode == AuthMode.SqlAuth)
        {
            if (string.IsNullOrEmpty(input.UserId))
                throw new ConfigValidationException("UserId is required when AuthMode is SqlAuth.");

            secretRef = SecretRefs.ForConnection(input.Name);
            if (input.Password is not null)
            {
                _secrets.Store(secretRef, input.Password);
            }
            else if (!_secrets.TryResolve(secretRef, out _))
            {
                throw new ConfigValidationException(
                    $"No password was provided and no existing credential is stored for connection '{input.Name}'.");
            }
        }

        var config = new ConnectionConfig
        {
            Name = input.Name,
            DriverType = input.DriverType,
            Host = input.Host,
            ConnectionString = input.ConnectionString,
            Port = input.Port,
            Database = input.Database,
            AuthMode = input.AuthMode,
            UserId = input.UserId,
            CredentialSecretRef = secretRef,
            Properties = input.Properties,
            Scripts = input.Scripts,
            Hooks = input.Hooks,
        };

        ValidateHooks(config.Hooks);

        var path = ConfigPaths.ConnectionFile(_configRoot, input.Name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        WriteAtomically(path, YamlConfigSerializer.Serialize(config));
        _git.CommitChanges([path], $"Save connection '{input.Name}'", author);

        return config;
    }

    public ConnectionConfig LoadConnection(string name)
    {
        var path = ConfigPaths.ConnectionFile(_configRoot, name);
        if (!File.Exists(path))
            throw new FileNotFoundException($"Connection '{name}' was not found.", path);

        return YamlConfigSerializer.Deserialize<ConnectionConfig>(File.ReadAllText(path));
    }

    public IReadOnlyList<string> ListConnections() =>
        ListFileNamesWithoutExtension(ConfigPaths.ConnectionsDir(_configRoot));

    public void DeleteConnection(string name, GitAuthor author)
    {
        var path = ConfigPaths.ConnectionFile(_configRoot, name);
        if (!File.Exists(path))
            return;

        var existing = YamlConfigSerializer.Deserialize<ConnectionConfig>(File.ReadAllText(path));
        if (existing.CredentialSecretRef is not null)
            _secrets.Delete(existing.CredentialSecretRef);

        File.Delete(path);
        _git.CommitChanges([path], $"Delete connection '{name}'", author);
    }

    // ---- Scripts ----

    /// <summary>
    /// Manifest and code are written and committed **together**, so history never shows a manifest
    /// describing code that was not yet there, or code with no manifest to say what it implements.
    /// </summary>
    public ScriptDefinition SaveScript(ScriptDefinition script, GitAuthor author)
    {
        ConfigValidation.ValidateName(script.Manifest.Name, nameof(script.Manifest.Name));

        var manifestPath = ConfigPaths.ScriptManifestFile(_configRoot, script.Manifest.Name);
        var codePath = ConfigPaths.ScriptCodeFile(_configRoot, script.Manifest.Name, script.Manifest.Language);
        Directory.CreateDirectory(ConfigPaths.ScriptsDir(_configRoot));

        WriteAtomically(manifestPath, YamlConfigSerializer.Serialize(script.Manifest));
        WriteAtomically(codePath, script.Code);

        // A script whose Language changed leaves its old code file behind under the other extension —
        // clean it up so a stale .cs doesn't linger beside a script that is now SQL, or vice versa.
        var otherLanguage = script.Manifest.Language == ScriptLanguage.Sql ? ScriptLanguage.CSharp : ScriptLanguage.Sql;
        var stalePath = ConfigPaths.ScriptCodeFile(_configRoot, script.Manifest.Name, otherLanguage);
        if (File.Exists(stalePath))
            File.Delete(stalePath);

        _git.CommitChanges([manifestPath, codePath], $"Save script '{script.Manifest.Name}'", author);

        return script;
    }

    public ScriptDefinition LoadScript(string name)
    {
        var manifestPath = ConfigPaths.ScriptManifestFile(_configRoot, name);
        if (!File.Exists(manifestPath))
            throw new FileNotFoundException($"Script '{name}' was not found.", manifestPath);

        var manifest = YamlConfigSerializer.Deserialize<ScriptConfig>(File.ReadAllText(manifestPath));
        var codePath = ConfigPaths.ScriptCodeFile(_configRoot, name, manifest.Language);
        return new ScriptDefinition
        {
            Manifest = manifest,
            // A manifest with no code beside it is a broken script, not an empty one — but it fails at
            // compile/validate with a message about the code rather than here with one about the file.
            Code = File.Exists(codePath) ? File.ReadAllText(codePath) : "",
        };
    }

    public IReadOnlyList<string> ListScripts() => ListFileNamesWithoutExtension(ConfigPaths.ScriptsDir(_configRoot));

    public void DeleteScript(string name, GitAuthor author)
    {
        var manifestPath = ConfigPaths.ScriptManifestFile(_configRoot, name);
        if (!File.Exists(manifestPath))
            return;

        var csPath = ConfigPaths.ScriptCodeFile(_configRoot, name, ScriptLanguage.CSharp);
        var sqlPath = ConfigPaths.ScriptCodeFile(_configRoot, name, ScriptLanguage.Sql);

        File.Delete(manifestPath);
        if (File.Exists(csPath))
            File.Delete(csPath);
        if (File.Exists(sqlPath))
            File.Delete(sqlPath);

        _git.CommitChanges([manifestPath, csPath, sqlPath], $"Delete script '{name}'", author);
    }

    // ---- Replication tasks ----

    public ReplicationTaskConfig SaveReplicationTask(ReplicationTaskConfig task, GitAuthor author)
    {
        ConfigValidation.ValidateName(task.Name, nameof(task.Name));
        ConfigValidation.ValidateScheduling(task.Scheduling, task.Name);
        ValidateHooks(task.Hooks);

        var path = ConfigPaths.TaskFile(_configRoot, task.Name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        WriteAtomically(path, YamlConfigSerializer.Serialize(task));
        _git.CommitChanges([path], $"Save replication task '{task.Name}'", author);

        return task;
    }

    public ReplicationTaskConfig LoadReplicationTask(string replicationName)
    {
        var path = ConfigPaths.TaskFile(_configRoot, replicationName);
        if (!File.Exists(path))
            throw new FileNotFoundException($"Replication task '{replicationName}' was not found.", path);

        return YamlConfigSerializer.Deserialize<ReplicationTaskConfig>(File.ReadAllText(path));
    }

    public IReadOnlyList<string> ListReplications()
    {
        var dir = ConfigPaths.ReplicationsDir(_configRoot);
        if (!Directory.Exists(dir))
            return [];

        return Directory.GetDirectories(dir)
            .Select(Path.GetFileName)
            .Where(n => n is not null)
            .Select(n => n!)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>Read-only git history for everything under this replication's config directory —
    /// the SPA's Config History view. Relies on the fixed configRoot == &lt;repoRoot&gt;/config
    /// convention used throughout (see ConfigPaths) to know the path relative to the repo root.</summary>
    public IReadOnlyList<CommitInfo> GetReplicationHistory(string replicationName, int limit = 50) =>
        _git.GetHistory($"config/replications/{replicationName}", limit);

    public void DeleteReplicationTask(string replicationName, GitAuthor author)
    {
        var dir = ConfigPaths.ReplicationDir(_configRoot, replicationName);
        if (!Directory.Exists(dir))
            return;

        var files = Directory.GetFiles(dir, "*.yaml", SearchOption.AllDirectories);
        Directory.Delete(dir, recursive: true);
        _git.CommitChanges(files, $"Delete replication task '{replicationName}'", author);
    }

    // ---- Table mappings ----

    public TableMappingConfig SaveTableMapping(string replicationName, TableMappingConfig mapping, GitAuthor author)
    {
        ConfigValidation.ValidateName(replicationName, nameof(replicationName));
        ConfigValidation.ValidateName(mapping.Name, nameof(mapping.Name));

        // A mapping that resolves to no connection or database cannot run. Catch it here rather than
        // at the first run, where it surfaces as a failed run instead of a rejected edit.
        EndpointResolution.Validate(LoadReplicationTask(replicationName), mapping);
        ValidateHooks(mapping.Hooks);

        var path = ConfigPaths.TableMappingFile(_configRoot, replicationName, mapping.Name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        WriteAtomically(path, YamlConfigSerializer.Serialize(mapping));
        _git.CommitChanges([path], $"Save table mapping '{mapping.Name}' on replication '{replicationName}'", author);

        return mapping;
    }

    public TableMappingConfig LoadTableMapping(string replicationName, string mappingName)
    {
        var path = ConfigPaths.TableMappingFile(_configRoot, replicationName, mappingName);
        if (!File.Exists(path))
            throw new FileNotFoundException(
                $"Table mapping '{mappingName}' was not found on replication '{replicationName}'.", path);

        return YamlConfigSerializer.Deserialize<TableMappingConfig>(File.ReadAllText(path));
    }

    public void DeleteTableMapping(string replicationName, string mappingName, GitAuthor author)
    {
        var path = ConfigPaths.TableMappingFile(_configRoot, replicationName, mappingName);
        if (!File.Exists(path))
            return;

        File.Delete(path);
        _git.CommitChanges([path], $"Delete table mapping '{mappingName}' on replication '{replicationName}'", author);
    }

    public IReadOnlyList<string> ListTableMappings(string replicationName) =>
        ListFileNamesWithoutExtension(ConfigPaths.TableMappingsDir(_configRoot, replicationName));

    private static IReadOnlyList<string> ListFileNamesWithoutExtension(string dir)
    {
        if (!Directory.Exists(dir))
            return [];

        return Directory.GetFiles(dir, "*.yaml")
            .Select(Path.GetFileNameWithoutExtension)
            .Where(n => n is not null)
            .Select(n => n!)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();
    }

    // ---- Hooks ----

    /// <summary>
    /// Catches the same class of mistake <c>EndpointResolution.Validate</c> catches for endpoints: a
    /// hook that cannot run is rejected here, while the operator is still looking at the edit, rather
    /// than discovered as a failed run. Checks every point's list: exactly one of <c>Sql</c>/<c>Hook</c>
    /// per entry, every token/parameter reference available at that point (<see cref="HookValidation"/>),
    /// and — for a reference to a named hook — that it resolves to a <see cref="ScriptLanguage.Sql"/>
    /// script, every required declared parameter is supplied, and no undeclared one is.
    /// </summary>
    private void ValidateHooks(Dictionary<string, List<HookConfig>?> hooks)
    {
        foreach (var (point, list) in hooks)
        {
            if (list is null)
                continue;

            if (!HookPoints.IsKnown(point))
                throw new ConfigValidationException(
                    $"'{point}' is not a hook point this build knows about. Known points: {string.Join(", ", HookPoints.All)}.");

            foreach (var hook in list)
                ValidateHook(point, hook);
        }
    }

    private void ValidateHook(string point, HookConfig hook)
    {
        var label = hook.Name ?? hook.Hook ?? "inline";

        if (hook.Sql is not null && hook.Hook is not null)
            throw new ConfigValidationException($"Hook '{label}' at '{point}' sets both 'sql' and 'hook' — exactly one is allowed.");
        if (hook.Sql is null && hook.Hook is null)
            throw new ConfigValidationException($"Hook '{label}' at '{point}' sets neither 'sql' nor 'hook' — exactly one is required.");

        if (hook.Sql is { } inlineSql)
        {
            var errors = HookValidation.ValidateAtPoint(point, inlineSql, []);
            if (errors.Count > 0)
                throw new ConfigValidationException($"Hook '{label}' at '{point}': {string.Join(" ", errors)}");
            return;
        }

        ScriptDefinition script;
        try
        {
            script = LoadScript(hook.Hook!);
        }
        catch (FileNotFoundException)
        {
            throw new ConfigValidationException($"Hook '{label}' at '{point}' references unknown script '{hook.Hook}'.");
        }

        if (script.Manifest.Language != ScriptLanguage.Sql)
            throw new ConfigValidationException(
                $"Hook '{label}' at '{point}' references script '{hook.Hook}', which is not a SQL hook.");

        var declaredNames = script.Manifest.Parameters.Select(p => p.Name).ToList();
        var bodyErrors = HookValidation.ValidateAtPoint(point, script.Code, declaredNames);
        if (bodyErrors.Count > 0)
            throw new ConfigValidationException($"Hook '{label}' at '{point}': {string.Join(" ", bodyErrors)}");

        foreach (var required in script.Manifest.Parameters.Where(p => p.Required))
            if (!hook.Parameters.ContainsKey(required.Name))
                throw new ConfigValidationException(
                    $"Hook '{label}' at '{point}' is missing required parameter '{required.Name}'.");

        foreach (var suppliedName in hook.Parameters.Keys)
            if (!declaredNames.Contains(suppliedName, StringComparer.Ordinal))
                throw new ConfigValidationException(
                    $"Hook '{label}' at '{point}' supplies parameter '{suppliedName}', which '{hook.Hook}' does not declare.");
    }

    /// <summary>
    /// Writes a config file so a concurrent reader sees either the old contents or the new, never a
    /// half-written file.
    /// <para>
    /// <see cref="File.WriteAllText(string, string?)"/> truncates and then writes, and this API serves
    /// reads straight off disk while other requests write — a GET landing inside that window returns
    /// an empty or truncated document. It is a small window and it is real: a Playwright poll caught
    /// one as <c>Unexpected end of JSON input</c>. Writing to a sibling temp file and renaming makes
    /// the swap atomic, because a rename within one directory is.
    /// </para>
    /// </summary>
    private static void WriteAtomically(string path, string contents)
    {
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, contents);
        File.Move(temporary, path, overwrite: true);
    }
}
