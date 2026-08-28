namespace DataSync.Core.Config;

public sealed class ConfigValidationException(string message) : Exception(message);

public static class ConfigValidation
{
    public static void ValidateName(string name, string paramName)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ConfigValidationException($"{paramName} must not be empty.");

        if (name.Any(c => !char.IsLetterOrDigit(c) && c != '-' && c != '_'))
            throw new ConfigValidationException(
                $"{paramName} '{name}' may only contain letters, digits, '-', and '_' (it becomes a file/directory name).");
    }

    /// <summary>
    /// A connection addresses its engine one way or the other: host and port, or an engine-native
    /// connection string. Both is ambiguous — nothing sensible decides which wins — and neither cannot
    /// connect at all.
    /// </summary>
    public static void ValidateAddressing(string? host, string? connectionString, string connectionName)
    {
        var hasHost = !string.IsNullOrWhiteSpace(host);
        var hasConnectionString = !string.IsNullOrWhiteSpace(connectionString);

        if (hasHost && hasConnectionString)
            throw new ConfigValidationException(
                $"Connection '{connectionName}' sets both a host and a connection string. Use one or the " +
                "other — with both, nothing decides which one is used.");

        if (!hasHost && !hasConnectionString)
            throw new ConfigValidationException(
                $"Connection '{connectionName}' needs either a host or a connection string.");

        if (hasConnectionString)
            RejectEmbeddedCredential(connectionString!, connectionName);
    }

    /// <summary>
    /// Keys that carry a secret in the connection-string syntaxes of the engines in scope. Matched as
    /// whole keys — as the token immediately before an <c>=</c> — so a *value* mentioning a password
    /// (a database called <c>PasswordVault</c>, a DSN path containing the word) is not rejected, and
    /// neither is <c>Persist Security Info</c>, which carries no secret.
    /// </summary>
    private static readonly string[] CredentialKeys =
        ["password", "pwd", "passwd", "secret", "accountkey", "apikey"];

    /// <summary>
    /// Config is git-committed and diffed in the UI. A password in a connection string would be
    /// committed, pushed and visible in the Version Control tab forever — which is the whole reason
    /// <see cref="ConnectionConfig.CredentialSecretRef"/> exists. The driver splices the resolved
    /// credential in at connect time instead.
    /// </summary>
    private static void RejectEmbeddedCredential(string connectionString, string connectionName)
    {
        foreach (var segment in connectionString.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = segment.IndexOf('=');
            if (separator < 0)
                continue;

            var key = segment[..separator].Trim().Replace(" ", "").Replace("_", "").ToLowerInvariant();
            if (!CredentialKeys.Contains(key))
                continue;

            throw new ConfigValidationException(
                $"Connection '{connectionName}' has '{segment[..separator].Trim()}' in its connection string. " +
                "Connections are stored in git and shown in the Version Control tab, so a credential there " +
                "would be committed and visible forever — set the password field instead and it is kept in " +
                "the secret store and applied when connecting.");
        }
    }
}