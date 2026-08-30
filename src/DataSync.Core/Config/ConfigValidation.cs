namespace DataSync.Core.Config;

public sealed class ConfigValidationException(string message) : Exception(message);

public static class ConfigValidation
{
    /// <summary>
    /// A name that is safe to use as a file or directory name, because it becomes one.
    /// <para>
    /// <c>.</c> is allowed, and deliberately: phase 45 infers a table mapping's name from its source as
    /// <c>schema.table</c>, which is what an operator would have typed anyway. It costs the two checks
    /// below — a dot is the one permitted character that can mean "somewhere else" rather than "part of
    /// a name".
    /// </para>
    /// </summary>
    public static void ValidateName(string name, string paramName)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ConfigValidationException($"{paramName} must not be empty.");

        if (name.Any(c => !char.IsLetterOrDigit(c) && c != '-' && c != '_' && c != '.'))
            throw new ConfigValidationException(
                $"{paramName} '{name}' may only contain letters, digits, '-', '_', and '.' " +
                "(it becomes a file/directory name).");

        // The traversal check, which is why allowing '.' is not free. '/' and '\' are already
        // excluded above, so ".." cannot walk anywhere on its own — but a path built from a name that
        // *is* ".." resolves to the parent directory on every platform, and there is no reason to find
        // out which caller composes paths carelessly.
        if (name.Contains("..", StringComparison.Ordinal))
            throw new ConfigValidationException($"{paramName} '{name}' must not contain '..'.");

        // A leading dot is a hidden file on Unix and a trailing one is invalid on Windows. Neither is
        // a name anybody means.
        if (name.StartsWith('.') || name.EndsWith('.'))
            throw new ConfigValidationException($"{paramName} '{name}' must not start or end with '.'.");
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

    /// <summary>
    /// A continuous replication's idle timeout has to outlast its frequency — **when somebody set
    /// one**.
    /// <para>
    /// Idle means "looked for changes and found none". An explicit timeout shorter than the interval
    /// between looks expires before the worker has looked even once, so the worker exits after every
    /// pass — the behaviour the timeout exists to stop, restored by a number. That pairing is a
    /// mistake and is refused.
    /// </para>
    /// <para>
    /// The check is deliberately **not** applied to the default. A replication that polls hourly is a
    /// perfectly ordinary thing to configure, and against the 60s default it means "run, wait a
    /// minute, exit, come back in an hour" — which is right, and which the rule would forbid. Somebody
    /// who wants a resident worker on an hourly frequency says so by setting the timeout, and then the
    /// rule holds them to it.
    /// </para>
    /// </summary>
    public static void ValidateScheduling(SchedulingConfig scheduling, string replicationName)
    {
        if (scheduling.Mode != ScheduleMode.Continuous)
            return;

        if (scheduling.FrequencySeconds is <= 0)
            throw new ConfigValidationException(
                $"Replication '{replicationName}' has a frequency of {scheduling.FrequencySeconds} seconds. " +
                "A continuous replication needs a positive frequency.");

        if (scheduling.IdleTimeoutSeconds is <= 0)
            throw new ConfigValidationException(
                $"Replication '{replicationName}' has an idle timeout of {scheduling.IdleTimeoutSeconds} seconds. " +
                "A worker that gives up after no time at all never gets as far as looking.");

        if (scheduling is { IdleTimeoutSeconds: { } idle, FrequencySeconds: { } frequency } && idle <= frequency)
            throw new ConfigValidationException(
                $"Replication '{replicationName}' sets an idle timeout of {idle}s against a frequency of " +
                $"{frequency}s. The idle timeout has to be longer than the frequency, or the worker gives up " +
                "before it has looked for changes even once.");
    }

    /// <summary>
    /// A historizing writer must not write into the table it is reading.
    /// <para>
    /// Snapshot appends a complete copy per pass and SCD Type 2 appends a version per change; pointed
    /// at their own source, both grow it without bound and the next pass reads what the last one
    /// wrote. There is no useful configuration here to preserve, and finding out at run time means
    /// finding out after the first pass has already doubled the table.
    /// </para>
    /// <para>
    /// Same connection is fine and needs nothing — it is the same *table object* that cannot work.
    /// </para>
    /// </summary>
    public static void ValidateHistorizedTarget(
        string writerKind, SourceTableRef source, TableRef target, string mappingName)
    {
        if (writerKind is not ("Snapshot" or "Scd2"))
            return;

        var same = string.Equals(source.ConnectionName, target.ConnectionName, StringComparison.OrdinalIgnoreCase)
            && string.Equals(source.Database, target.Database, StringComparison.OrdinalIgnoreCase)
            && string.Equals(source.Schema, target.Schema, StringComparison.OrdinalIgnoreCase)
            && string.Equals(source.Table, target.Table, StringComparison.OrdinalIgnoreCase);

        if (same)
            throw new ConfigValidationException(
                $"Table mapping '{mappingName}' writes to the table it reads with the '{writerKind}' " +
                "writer, which appends history rather than replacing rows. Each pass would grow the " +
                "source and the next would read what the last one wrote. Point it at a different table.");
    }
}
