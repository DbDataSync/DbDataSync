namespace DataSync.Core.Config;

/// <summary>Database engines a connection can target. Extended as new drivers are added (see Phase 8 backlog).</summary>
public enum ConnectionDriverType
{
    MsSql,
}

public enum AuthMode
{
    SqlAuth,
    IntegratedAuth,
}

/// <summary>
/// Persisted, git-tracked representation of a connection. Never carries a plaintext credential —
/// <see cref="CredentialSecretRef"/> is a key into ClrKernel.Core.Secrets.SecretStore, resolved at
/// connect time, never at config-load time.
/// </summary>
public sealed class ConnectionConfig
{
    public required string Name { get; set; }
    public required ConnectionDriverType DriverType { get; set; }
    public required string Host { get; set; }
    public int? Port { get; set; }
    public string? Database { get; set; }
    public required AuthMode AuthMode { get; set; }
    public string? UserId { get; set; }
    public string? CredentialSecretRef { get; set; }
    public Dictionary<string, string> Properties { get; set; } = new();
}

/// <summary>
/// Caller-supplied input for creating/updating a connection. Distinct from <see cref="ConnectionConfig"/>
/// so the type that ever touches a plaintext password is never the type that gets YAML-serialized.
/// </summary>
public sealed class ConnectionInput
{
    public required string Name { get; set; }
    public required ConnectionDriverType DriverType { get; set; }
    public required string Host { get; set; }
    public int? Port { get; set; }
    public string? Database { get; set; }
    public required AuthMode AuthMode { get; set; }
    public string? UserId { get; set; }

    /// <summary>Plaintext password. Null when not changing an existing SqlAuth connection's credential, or when AuthMode is IntegratedAuth.</summary>
    public string? Password { get; set; }

    public Dictionary<string, string> Properties { get; set; } = new();
}
