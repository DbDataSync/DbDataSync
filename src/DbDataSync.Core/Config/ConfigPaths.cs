namespace DbDataSync.Core.Config;

/// <summary>
/// The on-disk layout from architecture/detailed-design.md §3.6:
/// config/connections/&lt;name&gt;.yaml
/// config/replications/&lt;name&gt;/task.yaml
/// config/replications/&lt;name&gt;/table-mappings/&lt;mapping-name&gt;.yaml
/// config/scripts/&lt;name&gt;.yaml + config/scripts/&lt;name&gt;.cs
/// </summary>
internal static class ConfigPaths
{
    public static string ConnectionsDir(string configRoot) => Path.Combine(configRoot, "connections");

    public static string ConnectionFile(string configRoot, string name) =>
        Path.Combine(ConnectionsDir(configRoot), $"{name}.yaml");

    public static string ReplicationsDir(string configRoot) => Path.Combine(configRoot, "replications");

    public static string ReplicationDir(string configRoot, string replicationName) =>
        Path.Combine(ReplicationsDir(configRoot), replicationName);

    public static string TaskFile(string configRoot, string replicationName) =>
        Path.Combine(ReplicationDir(configRoot, replicationName), "task.yaml");

    public static string TableMappingsDir(string configRoot, string replicationName) =>
        Path.Combine(ReplicationDir(configRoot, replicationName), "table-mappings");

    public static string TableMappingFile(string configRoot, string replicationName, string mappingName) =>
        Path.Combine(TableMappingsDir(configRoot, replicationName), $"{mappingName}.yaml");

    public static string ScriptsDir(string configRoot) => Path.Combine(configRoot, "scripts");

    public static string ScriptManifestFile(string configRoot, string name) =>
        Path.Combine(ScriptsDir(configRoot), $"{name}.yaml");

    /// <summary>The code, beside the manifest. Two files rather than one because code embedded in YAML
    /// diffs badly, cannot be opened by an editor, and re-indents on every serializer round trip. The
    /// extension follows <see cref="ScriptLanguage"/> — a SQL hook is the same artifact with
    /// <c>.sql</c> beside the manifest instead of <c>.cs</c>.</summary>
    public static string ScriptCodeFile(string configRoot, string name, ScriptLanguage language = ScriptLanguage.CSharp) =>
        Path.Combine(ScriptsDir(configRoot), $"{name}{(language == ScriptLanguage.Sql ? ".sql" : ".cs")}");
}
