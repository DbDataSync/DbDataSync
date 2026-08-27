namespace DataSync.Core.Secrets;

/// <summary>
/// Naming convention for ClrKernel.Core.Secrets.SecretStore keys. Namespaced under "datasync:" since
/// the OS-native credential store is shared machine-wide with other apps that may use SecretStore too.
/// </summary>
public static class SecretRefs
{
    public static string ForConnection(string connectionName) => $"datasync:connection:{connectionName}";

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
