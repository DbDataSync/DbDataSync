namespace DbDataSync.Core.Config;

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
    /// <para>
    /// Internal rather than private since phase 79: <c>DbDataSyncConfigFile</c>'s writer calls this on
    /// <c>StateConnectionString</c> too, one detector shared rather than a second copy free to drift
    /// from the first — <c>connectionName</c> doubles as whatever value's name is being checked (a
    /// connection, or a <c>DbDataSync:*</c> key).
    /// </para>
    /// </summary>
    internal static void RejectEmbeddedCredential(string connectionString, string connectionName)
    {
        if (FindCredentialKeySegment(connectionString) is not { } segment)
            return;

        var separator = segment.IndexOf('=');
        throw new ConfigValidationException(
            $"Connection '{connectionName}' has '{segment[..separator].Trim()}' in its connection string. " +
            "Connections are stored in git and shown in the Version Control tab, so a credential there " +
            "would be committed and visible forever — set the password field instead and it is kept in " +
            "the secret store and applied when connecting.");
    }

    /// <summary>
    /// The non-throwing half of the same check — phase 81's admin screen needs to know whether a value
    /// it did **not** write (an environment variable or CLI argument an operator supplied directly)
    /// carries a credential, so it can mask it before it ever reaches the browser, without treating
    /// "carries a credential" itself as an error the way a write through this codebase's own config
    /// paths does. Same detector as <see cref="RejectEmbeddedCredential"/>, so the two can never
    /// disagree about what counts as a credential.
    /// </summary>
    public static bool ContainsEmbeddedCredential(string connectionString) =>
        FindCredentialKeySegment(connectionString) is not null;

    private static string? FindCredentialKeySegment(string connectionString)
    {
        foreach (var segment in connectionString.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = segment.IndexOf('=');
            if (separator < 0)
                continue;

            var key = segment[..separator].Trim().Replace(" ", "").Replace("_", "").ToLowerInvariant();
            if (CredentialKeys.Contains(key))
                return segment;
        }

        return null;
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
    /// <summary>
    /// The worker's degree of parallelism is a count of concurrent consumers, so it has to be at
    /// least one — a replication that processes zero mappings at a time processes none. The runner's
    /// own argument parser enforces the same floor (<c>TaskRunnerOptions</c>); this catches it at
    /// save, before a worker is ever spawned with a number it would reject on startup.
    /// </summary>
    public static void ValidateChangeProcessing(ChangeProcessingConfig changeProcessing, string replicationName)
    {
        if (changeProcessing.DegreeOfParallelism < 1)
            throw new ConfigValidationException(
                $"Replication '{replicationName}' sets a change-processing degree of parallelism of " +
                $"{changeProcessing.DegreeOfParallelism}. It has to be at least 1 — that is how many table " +
                "mappings the worker processes at once.");

        if (changeProcessing.BackfillDegreeOfParallelism < 1)
            throw new ConfigValidationException(
                $"Replication '{replicationName}' sets a backfill degree of parallelism of " +
                $"{changeProcessing.BackfillDegreeOfParallelism}. It has to be at least 1 — that is how many " +
                "backfill segments and verifications the worker processes at once.");
    }

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

    /// <summary>
    /// Phase 124's <c>KeyReconcile</c>/<c>KeyReconcileDelete</c> pair, save-time. Plain string literals
    /// rather than <c>DbDataSync.Drivers.Generic.GenericDriverKinds</c> constants — Core cannot
    /// reference the driver layer, which is what keeps config depending on drivers and not the other
    /// way round; <see cref="ValidateHistorizedTarget"/> makes the same choice for "Snapshot"/"Scd2".
    /// </summary>
    public static void ValidateKeyReconcilePairing(string readerKind, string writerKind, TableMappingConfig mapping)
    {
        const string keyReconcileReader = "KeyReconcile";
        const string keyReconcileWriter = "KeyReconcileDelete";

        var readerIsKeyReconcile = readerKind == keyReconcileReader;
        var writerIsKeyReconcileDelete = writerKind == keyReconcileWriter;

        if (readerIsKeyReconcile != writerIsKeyReconcileDelete)
            throw new ConfigValidationException(
                $"Table mapping '{mapping.Name}' pairs reader '{readerKind}' with writer '{writerKind}'. " +
                $"'{keyReconcileReader}' must always be paired with '{keyReconcileWriter}' — any other " +
                "combination would re-insert or corrupt rows a delete-diff sweep only ever means to remove.");

        if (!readerIsKeyReconcile)
            return;

        if (mapping.SourceColumns.Count == 0)
            throw new ConfigValidationException(
                $"Table mapping '{mapping.Name}' uses the '{keyReconcileReader}' reader, which needs the " +
                "source's cached primary key. Use Refresh metadata on this mapping first.");

        var keyColumns = mapping.SourceColumns.Where(c => c.IsPrimaryKey).ToList();
        if (keyColumns.Count == 0)
            throw new ConfigValidationException(
                $"Table mapping '{mapping.Name}' uses the '{keyReconcileReader}' reader, but its source " +
                "has no primary key. A keyless source can't be reconciled by key — use BatchReload + " +
                "DeleteInsert instead.");

        var mappedSourceColumns = mapping.ColumnMappings
            .Select(m => m.SourceColumn)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var unmapped = keyColumns.Where(c => !mappedSourceColumns.Contains(c.Name)).Select(c => c.Name).ToList();
        if (unmapped.Count > 0)
            throw new ConfigValidationException(
                $"Table mapping '{mapping.Name}' uses the '{keyReconcileReader}' reader, but its primary " +
                $"key column(s) {string.Join(", ", unmapped)} are not in its column mappings. Every source " +
                "key column must be mapped so the target side can be anti-joined on it.");
    }

    /// <summary>
    /// Phase 125: an enabled <see cref="ReconcileConfig"/> must resolve to the
    /// <c>KeyReconcile</c>/<c>KeyReconcileDelete</c> pair (reusing <see cref="ValidateKeyReconcilePairing"/>
    /// verbatim — a scheduled sweep needs exactly the same cached, fully-mapped primary key an on-demand
    /// one does), and an <see cref="AfterChangeStrategy"/> other than <see cref="NoAfterChangeStrategy"/>
    /// requires an explicit <see cref="ReconcileConfig.Every"/> cadence — resolved from the plan's own
    /// open question: the after-change floor (never firing more often than the cadence allows) has to be
    /// an explicit number, not an implied one.
    /// </summary>
    public static void ValidateReconcile(
        ReconcileConfig reconcile, TableMappingConfig mapping, string readerKind, string writerKind)
    {
        if (!reconcile.Enabled)
            return;

        ValidateKeyReconcilePairing(readerKind, writerKind, mapping);

        if (reconcile.Every is not null)
            ValidateScheduling(reconcile.Every, $"{mapping.Name} (reconcile)");

        if (reconcile.AfterChange is not NoAfterChangeStrategy && reconcile.Every is null)
            throw new ConfigValidationException(
                $"Table mapping '{mapping.Name}' enables delete reconciliation with an after-change " +
                "strategy but no 'every' cadence. After-change never fires more often than the cadence " +
                "allows, so a cadence has to be set even when the after-change trigger is the one that " +
                "actually matters.");
    }
}
