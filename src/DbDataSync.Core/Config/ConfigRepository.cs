using ClrKernel.Core.Secrets;
using DbDataSync.Core.Git;
using DbDataSync.Core.Secrets;

namespace DbDataSync.Core.Config;

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
                // Names the exact ref and the env var SecretStore would have fallen back to — the same
                // two things ConnectionsController.GetCredentialSource shows on the credential-source
                // panel, not just the connection name — since this is often the first place an operator
                // learns anything is missing at all.
                throw new ConfigValidationException(
                    $"No password was provided and no existing credential is stored for connection " +
                    $"'{input.Name}' (secret ref '{secretRef}', environment variable '{_secrets.EnvName(secretRef)}').");
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
            ConnectTimeoutSeconds = input.ConnectTimeoutSeconds,
            CommandTimeoutSeconds = input.CommandTimeoutSeconds,
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

        return YamlConfigSerializer.Deserialize<ConnectionConfig>(ReadAllTextAllowingConcurrentReplace(path));
    }

    public IReadOnlyList<string> ListConnections() =>
        ListFileNamesWithoutExtension(ConfigPaths.ConnectionsDir(_configRoot));

    public void DeleteConnection(string name, GitAuthor author)
    {
        var path = ConfigPaths.ConnectionFile(_configRoot, name);
        if (!File.Exists(path))
            return;

        var existing = YamlConfigSerializer.Deserialize<ConnectionConfig>(ReadAllTextAllowingConcurrentReplace(path));
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

        var manifest = YamlConfigSerializer.Deserialize<ScriptConfig>(ReadAllTextAllowingConcurrentReplace(manifestPath));
        var codePath = ConfigPaths.ScriptCodeFile(_configRoot, name, manifest.Language);
        return new ScriptDefinition
        {
            Manifest = manifest,
            // A manifest with no code beside it is a broken script, not an empty one — but it fails at
            // compile/validate with a message about the code rather than here with one about the file.
            Code = File.Exists(codePath) ? ReadAllTextAllowingConcurrentReplace(codePath) : "",
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
        ConfigValidation.ValidateChangeProcessing(task.ChangeProcessing, task.Name);
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

        return YamlConfigSerializer.Deserialize<ReplicationTaskConfig>(ReadAllTextAllowingConcurrentReplace(path));
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
        _git.GetHistory(ReplicationPrefix(replicationName), limit);

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

        var task = LoadReplicationTask(replicationName);
        ValidateTableMapping(task, mapping);

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

        return YamlConfigSerializer.Deserialize<TableMappingConfig>(ReadAllTextAllowingConcurrentReplace(path));
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
    /// <summary>
    /// Everything that has to be true of a mapping before it is written. Extracted from
    /// <see cref="SaveTableMapping"/> by phase 35, which needs the identical checks against a mapping
    /// it is about to restore rather than one it was handed — "a revert that produces config the tool
    /// would reject on save must not be reachable through a different door" is only a guarantee if the
    /// two doors run the same check, rather than two copies of it that can drift.
    /// </summary>
    private void ValidateTableMapping(ReplicationTaskConfig task, TableMappingConfig mapping)
    {
        // A mapping that resolves to no connection or database cannot run. Catch it here rather than
        // at the first run, where it surfaces as a failed run instead of a rejected edit.
        EndpointResolution.Validate(task, mapping);
        ValidateHooks(mapping.Hooks);

        // The mapping's own primary writer — computed once and reused below, both for the
        // already-existing historized-target check and for phase 129's Scd2-specific reconcile checks,
        // which need to know Kind *and* Options (a stated naturalKey) to validate a KeyReconcileScd2Close
        // pairing.
        var primaryWriter = PipelineResolution.Writer(task, mapping);

        // Same reasoning, one step further: a historizing writer pointed at its own source grows the
        // table on every pass, and finding out at run time means finding out after it has.
        if (mapping.Sources.Count == 1 && mapping.Targets.Count == 1)
        {
            ConfigValidation.ValidateHistorizedTarget(
                primaryWriter.Kind,
                EndpointResolution.ResolveSource(task, mapping.Sources[0]),
                EndpointResolution.ResolveTarget(task, mapping.Targets[0]),
                mapping.Name);
        }

        ConfigValidation.ValidateKeyReconcilePairing(
            PipelineResolution.Reader(task, mapping).Kind, PipelineResolution.Writer(task, mapping).Kind, mapping,
            primaryWriter.Kind, primaryWriter.Options);

        ConfigValidation.ValidateReconcile(
            PipelineResolution.Reconcile(task, mapping), mapping,
            PipelineResolution.ReconcileReaderKind(task, mapping), PipelineResolution.ReconcileWriterKind(task, mapping),
            primaryWriter.Kind, primaryWriter.Options);
    }


    /// <summary>Where this replication's config lives in the repository. One expression, because the
    /// history, both diffs and the restore all have to mean the same directory.</summary>
    private static string ReplicationPrefix(string replicationName) =>
        $"config/replications/{replicationName}";

    /// <summary>The patch one commit made to this replication — phase 35's "View changes".</summary>
    public ConfigDiff GetReplicationCommitDiff(
        string replicationName, string sha, int maxContentBytes = GitCommitService.DefaultMaxContentBytes) =>
        _git.GetCommitDiff(ReplicationPrefix(replicationName), sha, maxContentBytes);

    /// <summary>What <see cref="RestoreReplication"/> would change — the diff from now to then, which
    /// is what a confirmation has to show and is not the same set as the commit's own patch.</summary>
    public ConfigDiff GetReplicationRestoreDiff(
        string replicationName, string sha, int maxContentBytes = GitCommitService.DefaultMaxContentBytes) =>
        _git.GetRestoreDiff(ReplicationPrefix(replicationName), sha, maxContentBytes);

    /// <summary>
    /// Puts this replication's config back the way it was at <paramref name="sha"/>, and records that
    /// as a **new** commit — phase 35.
    ///
    /// <para>
    /// **A restore, not a `git revert`.** A revert computes an inverse patch and conflicts if anything
    /// touched the same lines since. Restoring the tree at a commit cannot conflict, and it is what an
    /// operator means by "put it back the way it was". History is never rewritten: the log shows what
    /// happened, including the undo.
    /// </para>
    ///
    /// <para>
    /// **Validated before anything is written.** A commit that predates a mapping restores to a state
    /// without it; one that predates a connection rename restores config naming a connection that no
    /// longer exists. So the whole restored set is parsed and put through the same validation a save
    /// would — a restore that produces config this tool would reject on save must not be reachable
    /// through a different door — and a refusal leaves the working tree untouched, because nothing has
    /// been written yet when it happens.
    /// </para>
    ///
    /// <para>
    /// **A dangling connection is a warning, not a refusal**, and that asymmetry is deliberate. Saving
    /// a mapping that names a connection which does not exist is allowed today — the connection may be
    /// about to be created — so refusing it here would make the restore stricter than the save it is
    /// restoring, which is the opposite of the rule above. It is still worth saying out loud, so it
    /// comes back as a warning the confirmation can show.
    /// </para>
    /// </summary>
    public ConfigRestoreResult RestoreReplication(string replicationName, string sha, GitAuthor author)
    {
        ConfigValidation.ValidateName(replicationName, nameof(replicationName));

        var prefix = ReplicationPrefix(replicationName);
        var restored = _git.GetTextFilesAt(prefix, sha);
        if (restored.Count == 0)
            throw new ConfigValidationException(
                $"Replication '{replicationName}' did not exist at commit '{sha}', so there is nothing " +
                "to restore it to. Restoring to a commit from before a replication was created would " +
                "mean deleting it, which is what the Delete action is for.");

        var taskPath = $"{prefix}/task.yaml";
        if (!restored.TryGetValue(taskPath, out var taskYaml))
            throw new ConfigValidationException(
                $"The config at commit '{sha}' has no {taskPath}, so replication '{replicationName}' " +
                "cannot be rebuilt from it.");

        var task = YamlConfigSerializer.Deserialize<ReplicationTaskConfig>(taskYaml);
        if (!string.Equals(task.Name, replicationName, StringComparison.Ordinal))
            throw new ConfigValidationException(
                $"The task.yaml at commit '{sha}' is for replication '{task.Name}', not " +
                $"'{replicationName}'. Restoring it here would leave a replication whose directory and " +
                "declared name disagree.");

        ValidateHooks(task.Hooks);

        var mappingPrefix = $"{prefix}/table-mappings/";
        var mappings = restored
            .Where(f => f.Key.StartsWith(mappingPrefix, StringComparison.Ordinal)
                        && f.Key.EndsWith(".yaml", StringComparison.Ordinal))
            .Select(f => YamlConfigSerializer.Deserialize<TableMappingConfig>(f.Value))
            .ToList();

        foreach (var mapping in mappings)
        {
            ConfigValidation.ValidateName(mapping.Name, nameof(mapping.Name));
            ValidateTableMapping(task, mapping);
        }

        // Read before writing, so the answer describes the restore rather than its aftermath.
        var diff = _git.GetRestoreDiff(prefix, sha);
        var warnings = DanglingConnectionWarnings(task, mappings);

        // Everything that has to be staged: what the restore writes, and what it removes. A file on
        // disk that is not in the restored set is one created after that commit, and leaving it would
        // make the result neither the old state nor the new one.
        var root = _git.RepositoryRoot;
        var touched = new List<string>();

        var replicationDir = ConfigPaths.ReplicationDir(_configRoot, replicationName);
        if (Directory.Exists(replicationDir))
        {
            // *.yaml, which is everything this layer writes — deliberately not every file. Making the
            // directory match the tree exactly would also delete whatever else happens to be sitting
            // there (an editor backup, a note somebody left), and deleting a file this tool never
            // created is not what "put the config back" asks for.
            foreach (var existing in Directory.GetFiles(replicationDir, "*.yaml", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(root, existing).Replace('\\', '/');
                if (restored.ContainsKey(relative))
                    continue;

                File.Delete(existing);
                touched.Add(existing);
            }
        }

        foreach (var (relative, content) in restored)
        {
            var absolute = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(absolute)!);
            WriteAtomically(absolute, content);
            touched.Add(absolute);
        }

        // diff.Sha rather than the caller's, which may have been an abbreviation — the message an
        // operator later reads in the log should name the commit, not however much of it was typed.
        var commitSha = _git.CommitChanges(
            touched, $"Restore replication '{replicationName}' to {Short(diff.Sha)}", author);

        return new ConfigRestoreResult(diff.Sha, commitSha, diff.Changes, warnings);
    }

    /// <summary>
    /// Connections a restored mapping names that no connection file exists for. Not a refusal — see
    /// <see cref="RestoreReplication"/> — but the single most likely way a restore lands a replication
    /// that cannot run, so it is worth a sentence in the confirmation rather than a surprise at the
    /// next pass.
    /// </summary>
    private List<string> DanglingConnectionWarnings(ReplicationTaskConfig task, IReadOnlyList<TableMappingConfig> mappings)
    {
        var known = ListConnections().ToHashSet(StringComparer.OrdinalIgnoreCase);
        var referenced = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var mapping in mappings)
        {
            foreach (var source in mapping.Sources)
                referenced.Add(EndpointResolution.ResolveSource(task, source).ConnectionName);
            foreach (var target in mapping.Targets)
                referenced.Add(EndpointResolution.ResolveTarget(task, target).ConnectionName);
        }

        return [.. referenced
            .Where(name => !known.Contains(name))
            .Select(name =>
                $"The restored config references connection '{name}', which does not exist. Mappings " +
                "using it cannot run until it is created.")];
    }

    private static string Short(string sha) => sha.Length > 8 ? sha[..8] : sha;

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
        ReplaceAllowingConcurrentReaders(temporary, path);
    }

    /// <summary>
    /// The write half's own Windows problem, and the mirror image of
    /// <see cref="ReadAllTextAllowingConcurrentReplace"/>: granting readers
    /// <see cref="FileShare.Delete"/> was necessary but not sufficient.
    /// <para>
    /// <see cref="File.Move(string, string, bool)"/> with <c>overwrite</c> becomes <c>MoveFileEx</c>
    /// with <c>MOVEFILE_REPLACE_EXISTING</c> on Windows, and that call replaces the destination by
    /// *deleting* it first. Deleting a file on Windows only ever marks it for deletion — the name stays
    /// in the directory until the last handle closes — so the rename that follows finds the name still
    /// taken and fails with <c>ERROR_ACCESS_DENIED</c>. A reader holding the file open with every share
    /// flag there is cannot prevent that, which is why the read-side fix did not finish the job. POSIX
    /// <c>rename()</c> has no such step, so Linux never sees this and the guarantee read as complete.
    /// </para>
    /// <para>
    /// Measured rather than assumed: with one thread replacing the file as fast as it can and another
    /// reading it as fast as it can, an unretried move fails on its very first attempt, and this loop
    /// completed 352 replacements against 9,208 concurrent reads without one failure. A reader holds the
    /// file for microseconds, so the window this is waiting for opens almost immediately; the budget is
    /// generous only so that a pathological burst degrades into a pause rather than a failed config save.
    /// Each individual attempt is still an atomic replace, so a reader continues to see one whole version
    /// or the other — nothing here weakens the promise the method name makes.
    /// </para>
    /// <para>
    /// Not gated to Windows: on Linux the first attempt always succeeds and the loop costs nothing, and
    /// a platform check here would be a second thing to be wrong about.
    /// </para>
    /// </summary>
    private static void ReplaceAllowingConcurrentReaders(string temporary, string path)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                File.Move(temporary, path, overwrite: true);
                return;
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                if (DateTime.UtcNow >= deadline)
                    throw;

                // Spin briefly before yielding: the common case is a reader that is already most of the
                // way through a file of a few hundred bytes, and sleeping a whole millisecond for that
                // would make a burst of saves far slower than it needs to be.
                if (attempt < 20)
                    Thread.SpinWait(50);
                else
                    Thread.Sleep(1);
            }
        }
    }

    /// <summary>
    /// The read half of the same atomicity <see cref="WriteAtomically"/> promises — found missing by a
    /// real, reproducible Windows-only failure in <c>ConcurrentConfigReadTests</c>:
    /// <see cref="File.ReadAllText(string)"/> opens with the BCL default <see cref="FileShare.Read"/>,
    /// which does not include <see cref="FileShare.Delete"/>. POSIX <c>rename()</c> (what
    /// <see cref="File.Move(string, string, bool)"/> becomes on Linux) can always replace a path even
    /// while another process holds it open — existing handles just keep pointing at the old inode — but
    /// Windows' <c>MoveFileEx</c> refuses to replace a file that has any open handle lacking
    /// <c>FILE_SHARE_DELETE</c>, throwing "being used by another process." A reader that merely opened
    /// with the default share mode was, without meaning to, blocking the exact atomic swap this class
    /// exists to make safe. Explicit <see cref="FileShare.ReadWrite"/> alongside <see cref="FileShare.Delete"/>
    /// matches what a concurrent atomic write needs to still succeed while this read is in flight.
    /// </summary>
    private static string ReadAllTextAllowingConcurrentReplace(string path)
    {
        using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
