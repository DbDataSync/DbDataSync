namespace DataSync.Core.Config;

/// <summary>
/// The on-disk layout from architecture/detailed-design.md §3.6:
/// config/connections/&lt;name&gt;.yaml
/// config/replications/&lt;name&gt;/task.yaml
/// config/replications/&lt;name&gt;/table-mappings/&lt;mapping-name&gt;.yaml
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
}
