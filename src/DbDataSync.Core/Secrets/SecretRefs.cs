namespace DbDataSync.Core.Secrets;

/// <summary>
/// Naming convention for ClrKernel.Core.Secrets.SecretStore keys. Namespaced under "dbdatasync:" since
/// the OS-native credential store is shared machine-wide with other apps that may use SecretStore too.
/// </summary>
public static class SecretRefs
{
    public static string ForConnection(string connectionName) => $"dbdatasync:connection:{connectionName}";

    /// <summary>
    /// The secret ref for a <c>DbDataSync:*</c> app setting that must never be written into
    /// <c>dbdatasync.config.yaml</c> itself — <c>StateConnectionString</c>'s password, so far, the only
    /// one. Fixed and non-overridable: unlike <see cref="ForConnection"/>, this is not a name an
    /// operator chooses per connection, it is the one ref phase 79's starter file documents, so there
    /// is exactly one thing to remember and exactly one command that sets it
    /// (<c>dbdatasync secret set dbdatasync:config:&lt;key&gt; "..."</c>).
    /// </summary>
    public static string ForAppSetting(string key) => $"dbdatasync:config:{key}";

    /// <summary>
    /// The environment variable SecretStore falls back to when no OS keyring is available — the
    /// ref uppercased with every non-alphanumeric character replaced, under
    /// <c>CLRKERNEL_SECRET_</c>. Defined here rather than restated per caller because it is the value
    /// an operator staring at an auth failure needs to be told, and a subtly different spelling of it
    /// is worse than none.
    /// </summary>
    public static string EnvironmentVariableFor(string secretRef)
    {
        var sanitized = new string(secretRef.ToUpperInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '_').ToArray());
        return $"CLRKERNEL_SECRET_{sanitized}";
    }
}
