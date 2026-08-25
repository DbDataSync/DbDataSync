namespace DataSync.Core.Secrets;

/// <summary>
/// Naming convention for ClrKernel.Core.Secrets.SecretStore keys. Namespaced under "datasync:" since
/// the OS-native credential store is shared machine-wide with other apps that may use SecretStore too.
/// </summary>
public static class SecretRefs
{
    public static string ForConnection(string connectionName) => $"datasync:connection:{connectionName}";
}
