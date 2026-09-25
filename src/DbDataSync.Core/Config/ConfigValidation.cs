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

        if (changeProcessing.BulkLoadDegreeOfParallelism < 1)
            throw new ConfigValidationException(
                $"Replication '{replicationName}' sets a bulk load degree of parallelism of " +
                $"{changeProcessing.BulkLoadDegreeOfParallelism}. It has to be at least 1 — that is how many " +
                "bulk load segments and verifications the worker processes at once.");
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
    /// Phase 124's <c>KeyReconcile</c>/<c>KeyReconcileDelete</c> pair, save-time, widened by phase 129
    /// to also accept <c>KeyReconcileScd2Close</c> as the paired writer — and, only for that ending,
    /// two more checks that need the mapping's own primary writer. Plain string literals rather than
    /// <c>DbDataSync.Drivers.Generic.GenericDriverKinds</c> constants — Core cannot reference the
    /// driver layer, which is what keeps config depending on drivers and not the other way round;
    /// <see cref="ValidateHistorizedTarget"/> makes the same choice for "Snapshot"/"Scd2".
    /// <para>
    /// <paramref name="primaryWriterKind"/>/<paramref name="primaryWriterOptions"/> are the mapping's
    /// own <see cref="PipelineResolution.Writer"/> — not the reconcile writer being validated — needed
    /// only to check that a <c>KeyReconcileScd2Close</c> pairing makes sense: closing a version is
    /// meaningless unless the mapping's own writer is <c>Scd2</c>, and a stated natural key has to be
    /// the same columns <c>KeyReconcileReader</c> actually stages.
    /// </para>
    /// </summary>
    public static void ValidateKeyReconcilePairing(
        string readerKind, string writerKind, TableMappingConfig mapping,
        string primaryWriterKind, IReadOnlyDictionary<string, string> primaryWriterOptions)
    {
        const string keyReconcileReader = "KeyReconcile";
        const string keyReconcileDeleteWriter = "KeyReconcileDelete";
        const string keyReconcileScd2CloseWriter = "KeyReconcileScd2Close";

        var readerIsKeyReconcile = readerKind == keyReconcileReader;
        var writerIsValidPair = writerKind == keyReconcileDeleteWriter || writerKind == keyReconcileScd2CloseWriter;

        if (readerIsKeyReconcile != writerIsValidPair)
            throw new ConfigValidationException(
                $"Table mapping '{mapping.Name}' pairs reader '{readerKind}' with writer '{writerKind}'. " +
                $"'{keyReconcileReader}' must always be paired with '{keyReconcileDeleteWriter}' or " +
                $"'{keyReconcileScd2CloseWriter}' — any other combination would re-insert or corrupt rows " +
                "a delete-diff sweep only ever means to remove or close.");

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

        if (writerKind != keyReconcileScd2CloseWriter)
            return;

        if (primaryWriterKind != "Scd2")
            throw new ConfigValidationException(
                $"Table mapping '{mapping.Name}' pairs '{keyReconcileScd2CloseWriter}' with a primary " +
                $"writer of '{primaryWriterKind}', not 'Scd2'. Closing a version on a target that isn't " +
                "versioned is meaningless — this writer only pairs with a mapping whose own writer is Scd2.");

        if (primaryWriterOptions.TryGetValue("naturalKey", out var stated) && !string.IsNullOrWhiteSpace(stated))
        {
            var statedKeys = stated.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var derivedTargetNames = keyColumns
                .Select(c => mapping.ColumnMappings.First(
                    m => string.Equals(m.SourceColumn, c.Name, StringComparison.OrdinalIgnoreCase)).TargetColumn)
                .ToList();

            if (!statedKeys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase)
                    .SequenceEqual(derivedTargetNames.OrderBy(k => k, StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase))
                throw new ConfigValidationException(
                    $"Table mapping '{mapping.Name}' states an Scd2 natural key ({string.Join(", ", statedKeys)}) " +
                    $"that differs from the source's primary key ({string.Join(", ", derivedTargetNames)}) — the " +
                    $"only columns '{keyReconcileReader}' actually stages. '{keyReconcileScd2CloseWriter}' would " +
                    "join on columns the staging table doesn't have. Either drop the custom natural key, or keep " +
                    "this mapping on the status quo (no SCD2 delete detection) until a future phase lets " +
                    $"'{keyReconcileReader}' stage an arbitrary column list.");
        }
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
        ReconcileConfig reconcile, TableMappingConfig mapping, string readerKind, string writerKind,
        string primaryWriterKind, IReadOnlyDictionary<string, string> primaryWriterOptions)
    {
        if (!reconcile.Enabled)
            return;

        ValidateKeyReconcilePairing(readerKind, writerKind, mapping, primaryWriterKind, primaryWriterOptions);

        if (reconcile.Every is not null)
            ValidateScheduling(reconcile.Every, $"{mapping.Name} (reconcile)");

        if (reconcile.AfterChange is not NoAfterChangeStrategy && reconcile.Every is null)
            throw new ConfigValidationException(
                $"Table mapping '{mapping.Name}' enables delete reconciliation with an after-change " +
                "strategy but no 'every' cadence. After-change never fires more often than the cadence " +
                "allows, so a cadence has to be set even when the after-change trigger is the one that " +
                "actually matters.");
    }

    /// <summary>
    /// Phase 190S. Every source names exactly one of a table or a query — never both, never neither —
    /// and every target names a table: there is no query alternative for a write destination.
    /// </summary>
    public static void ValidateSources(TableMappingConfig mapping)
    {
        for (var i = 0; i < mapping.Sources.Count; i++)
        {
            var source = mapping.Sources[i];
            var hasTable = !string.IsNullOrWhiteSpace(source.Table);
            var hasQuery = !string.IsNullOrWhiteSpace(source.Query);
            if (hasTable == hasQuery)
                throw new ConfigValidationException(
                    $"Table mapping '{mapping.Name}' source {i} must name exactly one of a table or a " +
                    $"query — it currently names {(hasTable ? "both" : "neither")}.");
        }

        for (var i = 0; i < mapping.Targets.Count; i++)
        {
            if (string.IsNullOrWhiteSpace(mapping.Targets[i].Table))
                throw new ConfigValidationException(
                    $"Table mapping '{mapping.Name}' target {i} names no table. A target is always a " +
                    "real table — there is no query alternative for a write destination.");
        }
    }

    /// <summary>
    /// Phase 190S. A query-shaped source has two more save-time rules than a table-shaped one, both
    /// needing the mapping's *effective reader* rather than being shape-only like
    /// <see cref="ValidateSources"/> above:
    /// <para>
    /// **Hard, always, no degraded mode**: a <c>Watermark</c> reader against a source with
    /// <see cref="SourceTableSpec.AllowSubquery"/> <c>false</c> is rejected outright. Watermark reading
    /// needs <c>ORDER BY</c> for its tie-safe bounded-read guarantee on essentially every pass, including
    /// the first, so wrapping the query is not optional the way it is for a reload-style pass — there is
    /// nothing to silently fall back to.
    /// </para>
    /// <para>
    /// **Reader support**: only <c>BatchReload</c>/<c>Watermark</c> (generic or MsSql's own
    /// <c>MsSqlBatchReload</c> Kind string, which resolves to the same reader — phase 191S) know how to
    /// read a query-shaped source at all; every other reader still assumes a real table it can address
    /// directly (Change Tracking, CDC, trigger-audit, Flashback, logical decoding, KeyReconcile). These are
    /// bare string literals rather than a reference to <c>DbDataSync.Drivers.Generic</c>'s own Kind
    /// constants, matching <see cref="PipelineResolution"/>'s own precedent (its Reconcile-Kind defaults) —
    /// this project is upstream of the driver projects that define those constants and cannot reference them.
    /// </para>
    /// </summary>
    public static void ValidateQuerySourceReader(TableMappingConfig mapping, string readerKind)
    {
        var querySources = mapping.Sources.Where(s => s.Query is not null).ToList();
        if (querySources.Count == 0)
            return;

        if (readerKind == "Watermark" && querySources.Any(s => !s.AllowSubquery))
            throw new ConfigValidationException(
                $"Table mapping '{mapping.Name}' uses the Watermark reader against a query-shaped source " +
                "that disallows subqueries. Watermark reading always needs to wrap the query, so this " +
                "combination cannot run — allow subqueries for this source, or choose a different reader.");

        if (readerKind is not ("BatchReload" or "Watermark" or "MsSqlBatchReload"))
            throw new ConfigValidationException(
                $"Table mapping '{mapping.Name}' has a query-shaped source, but its reader ('{readerKind}') " +
                "does not support one — only a reload or Watermark reader can read from a query. Use one " +
                "of those, or switch this source back to a table.");
    }

    /// <summary>
    /// Phase 192S (190S's own deferred decision 7). A reconciling writer's delete-scope predicate is
    /// always built by translating the segment/watermark column's *name* to its target-side one and
    /// reusing the same bound values verbatim — see <c>DeleteInsertWriter</c>/
    /// <c>KeyReconcileDeleteWriter</c>/<c>MsSqlMergeReconcileWriter</c>/<c>MsSqlDeleteInsertWriter</c>.
    /// That translation needs a real <see cref="ColumnMapping"/> to translate *through* — a segment or
    /// watermark column that isn't mapped at all has no target-side name for the writer to bind against.
    /// Checked against the mapping's own statically-configured reader options only (a default segment, or
    /// <c>watermarkColumn</c>) — a Bulk Load's own per-work-item segment is assigned at enqueue time, not
    /// part of this saved config, and isn't reachable here.
    /// <para>
    /// These are bare string literals for the same layering reason <see cref="ValidateQuerySourceReader"/>'s
    /// own are: this project cannot reference the driver projects that define the real Kind constants.
    /// </para>
    /// </summary>
    public static void ValidateReconcileScopeColumn(
        TableMappingConfig mapping, string readerKind, string writerKind, IReadOnlyDictionary<string, string> readerOptions)
    {
        if (writerKind is not
            ("DeleteInsert" or "KeyReconcileDelete" or "KeyReconcileScd2Close" or "MsSqlMergeReconcile" or "MsSqlDeleteInsert"))
            return;

        // Phase 195S: a segment or watermark can now name a relationship's own column instead of the
        // primary source's — the mapped-column check below has to match on that same relationship, or a
        // relationship-sourced column would either false-negative against an unrelated primary mapping
        // sharing its name, or false-positive as "mapped" when it isn't.
        string? column;
        string? relationship;
        if (readerKind == "Watermark")
        {
            column = readerOptions.GetValueOrDefault("watermarkColumn");
            relationship = readerOptions.GetValueOrDefault("watermarkRelationship");
            if (string.IsNullOrWhiteSpace(relationship))
                relationship = null;
        }
        else
        {
            (column, relationship) = SegmentSerializer.ReadOptional(readerOptions) switch
            {
                ListSegment list => (list.Column, list.Relationship),
                RangeSegment range => (range.Column, range.Relationship),
                AutoSegment auto => (auto.Column, auto.Relationship),
                _ => (null, null),
            };
        }

        if (column is null)
            return;

        var mapped = mapping.ColumnMappings.Any(m =>
            string.Equals(m.Relationship, relationship, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(m.SourceColumn, column, StringComparison.OrdinalIgnoreCase));
        if (!mapped)
            throw new ConfigValidationException(
                $"Table mapping '{mapping.Name}' reads with '{readerKind}' scoped by column '{column}'" +
                (relationship is null ? "" : $" on relationship '{relationship}'") + ", and " +
                $"writes with the reconciling writer '{writerKind}'. A reconciling writer needs a real target " +
                $"column to bind its delete-scope predicate against, so '{column}' must also have a column " +
                "mapping — add one, or choose a non-reconciling writer.");
    }

    /// <summary>
    /// Phase 186J. Three checks, all shape-level — whether a <see cref="RelationshipConfig"/>'s
    /// <see cref="RelationshipJoinKey"/> columns actually exist is a save-time metadata-refresh concern,
    /// the same way <see cref="ColumnMapping.SourceColumn"/>/<see cref="ColumnMapping.TargetColumn"/>
    /// are never checked against a live catalog here either.
    /// </summary>
    public static void ValidateRelationships(TableMappingConfig mapping)
    {
        var seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var relationship in mapping.Relationships)
        {
            if (!seenNames.Add(relationship.Name))
                throw new ConfigValidationException(
                    $"Table mapping '{mapping.Name}' declares more than one relationship named " +
                    $"'{relationship.Name}'. Relationship names must be unique within a mapping.");

            if (relationship.JoinKeys.Count == 0)
                throw new ConfigValidationException(
                    $"Table mapping '{mapping.Name}' declares relationship '{relationship.Name}' with no " +
                    "join keys. A relationship needs at least one local/foreign column pair to join on.");
        }

        var declaredNames = mapping.Relationships.Select(r => r.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var unknown = mapping.ColumnMappings
            .Where(c => c.Relationship is not null && !declaredNames.Contains(c.Relationship))
            .Select(c => c.Relationship!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (unknown.Count > 0)
            throw new ConfigValidationException(
                $"Table mapping '{mapping.Name}' has a column mapping referencing relationship(s) " +
                $"{string.Join(", ", unknown.Select(n => $"'{n}'"))}, which {(unknown.Count == 1 ? "is" : "are")} " +
                "not declared on this mapping.");
    }
}
