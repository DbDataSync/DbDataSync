using DbDataSync.Core.Sql;

namespace DbDataSync.Core.Config;

/// <summary>Database engines a connection can target. Extended as new drivers are added (see implementation-plan.md's Backlog section).</summary>
public enum ConnectionDriverType
{
    MsSql,
    Postgres,

    /// <summary>
    /// DuckDB, embedded. Unlike the other two it is not a server: the "address" is a file path or
    /// <c>:memory:</c>, there is nothing to authenticate to, and there is no catalog worth browsing
    /// because what a DuckDB source reads is a query an operator wrote — see
    /// <c>DbDataSync.Drivers.DuckDb.DuckDbQueryReader</c>.
    /// </summary>
    DuckDb,
}

/// <summary>
/// What DbDataSync supplies when connecting. Deliberately not an enumeration of every engine's mechanism
/// — wallets, <c>.pgpass</c>, Kerberos ticket caches, DSN-stored credentials, auth plugins — because
/// that list would be wrong the day it was written and extended forever. Three members that describe
/// *our* side of it.
/// </summary>
public enum AuthMode
{
    /// <summary>A user id, and a credential resolved from the secret store at connect time.</summary>
    SqlAuth,

    /// <summary>The process's own OS identity — Windows integrated security, Kerberos.</summary>
    IntegratedAuth,

    /// <summary>
    /// DbDataSync supplies nothing. What an Oracle wallet, a DSN with stored credentials, a
    /// <c>.pgpass</c> file and a credential-bearing JDBC URL all look like from here: the address or
    /// the environment provides it. Anything more specific is a per-engine detail and belongs in
    /// <see cref="ConnectionConfig.Properties"/>.
    /// </summary>
    None,
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

    /// <summary>Host-mode addressing. Null when <see cref="ConnectionString"/> is used instead —
    /// exactly one of the two, enforced at save (see <c>ConfigValidation.ValidateAddressing</c>).</summary>
    public string? Host { get; set; }

    public int? Port { get; set; }

    /// <summary>
    /// The engine-native address, for a connection that host and port cannot express: an ODBC DSN or
    /// full connection string, a JDBC URL, an Oracle EZConnect or TNS name — or a SQL Server string
    /// carrying a failover partner or <c>MultiSubnetFailover</c>.
    /// <para>
    /// **Never carries a credential.** Config is git-committed and diffed in the UI; a password here
    /// would be committed, pushed and visible in the Version Control tab forever. Save-time validation
    /// rejects one, and the driver splices the resolved credential in at connect time exactly as it
    /// does for host mode.
    /// </para>
    /// </summary>
    public string? ConnectionString { get; set; }

    /// <summary>Which database to work in. Allowed with either addressing mode: "how do I connect" and
    /// "which database" are separate questions, and <c>SqlDialect.UseDatabaseAsync</c> already answers
    /// the second.</summary>
    public string? Database { get; set; }

    public required AuthMode AuthMode { get; set; }
    public string? UserId { get; set; }
    public string? CredentialSecretRef { get; set; }

    /// <summary>
    /// How long to wait for the connection itself to open, in seconds. <c>null</c> means the default
    /// (<see cref="ConnectionTimeouts.DefaultConnectSeconds"/>); <c>0</c> means unlimited; a positive
    /// value is seconds.
    /// <para>
    /// <c>0</c> is not an invented sentinel — <c>Connect Timeout=0</c> is documented as infinite by
    /// both <c>Microsoft.Data.SqlClient</c> and Npgsql, so the configured value is passed straight to
    /// the connection-string builder with no translation in between.
    /// </para>
    /// </summary>
    public int? ConnectTimeoutSeconds { get; set; }

    /// <summary>
    /// How long any one command against this connection may run, in seconds. Same shape as
    /// <see cref="ConnectTimeoutSeconds"/> — <c>null</c> is the default
    /// (<see cref="ConnectionTimeouts.DefaultCommandSeconds"/>), <c>0</c> is unlimited.
    /// <para>
    /// Unlike connect timeout there is no connection-string key for this on either provider: it is a
    /// runtime property on each <c>DbCommand</c>. <see cref="ConnectionTimeouts"/> is what carries the
    /// resolved value from here to every command issued against a source or target.
    /// </para>
    /// </summary>
    public int? CommandTimeoutSeconds { get; set; }

    public Dictionary<string, string> Properties { get; set; } = new();

    /// <summary>Scripts bound at this level, keyed by slot (see <c>ScriptSlots</c>). An absent key
    /// inherits from a broader level; a key present with a null value is "explicitly none" and
    /// overrides an inherited binding. See <see cref="ScriptResolution"/>.</summary>
    public Dictionary<string, ScriptBinding?> Scripts { get; set; } = new();

    /// <summary>Hooks bound at this level, keyed by point (see <c>HookPoints</c>). See
    /// <see cref="HookResolution"/> — this is the level every mapping against this connection inherits
    /// from unless it or its replication overrides.</summary>
    public Dictionary<string, List<HookConfig>?> Hooks { get; set; } = new();
}

/// <summary>
/// Caller-supplied input for creating/updating a connection. Distinct from <see cref="ConnectionConfig"/>
/// so the type that ever touches a plaintext password is never the type that gets YAML-serialized.
/// </summary>
public sealed class ConnectionInput
{
    public required string Name { get; set; }
    public required ConnectionDriverType DriverType { get; set; }
    public string? Host { get; set; }
    public int? Port { get; set; }

    /// <inheritdoc cref="ConnectionConfig.ConnectionString"/>
    public string? ConnectionString { get; set; }

    public string? Database { get; set; }
    public required AuthMode AuthMode { get; set; }
    public string? UserId { get; set; }

    /// <summary>Plaintext password. Null when not changing an existing SqlAuth connection's credential, or when AuthMode is IntegratedAuth.</summary>
    public string? Password { get; set; }

    /// <inheritdoc cref="ConnectionConfig.ConnectTimeoutSeconds"/>
    public int? ConnectTimeoutSeconds { get; set; }

    /// <inheritdoc cref="ConnectionConfig.CommandTimeoutSeconds"/>
    public int? CommandTimeoutSeconds { get; set; }

    public Dictionary<string, string> Properties { get; set; } = new();

    /// <inheritdoc cref="ConnectionConfig.Scripts"/>
    public Dictionary<string, ScriptBinding?> Scripts { get; set; } = new();

    /// <inheritdoc cref="ConnectionConfig.Hooks"/>
    public Dictionary<string, List<HookConfig>?> Hooks { get; set; } = new();
}
