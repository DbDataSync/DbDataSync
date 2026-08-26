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
            Port = input.Port,
            Database = input.Database,
            AuthMode = input.AuthMode,
            UserId = input.UserId,
            CredentialSecretRef = secretRef,
            Properties = input.Properties,
        };

        var path = ConfigPaths.ConnectionFile(_configRoot, input.Name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, YamlConfigSerializer.Serialize(config));
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

    // ---- Replication tasks ----

    public ReplicationTaskConfig SaveReplicationTask(ReplicationTaskConfig task, GitAuthor author)
    {
        ConfigValidation.ValidateName(task.Name, nameof(task.Name));

        var path = ConfigPaths.TaskFile(_configRoot, task.Name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, YamlConfigSerializer.Serialize(task));
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

        var path = ConfigPaths.TableMappingFile(_configRoot, replicationName, mapping.Name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, YamlConfigSerializer.Serialize(mapping));
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
}
