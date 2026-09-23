using System.Data.Common;
using System.Diagnostics;
using DbDataSync.Api.Auth;
using DbDataSync.Core.Config;
using DbDataSync.Core.Git;
using DbDataSync.Drivers.Abstractions;
using DbDataSync.Drivers.Generic;
using DbDataSync.Core.Sql;

namespace DbDataSync.Api.Services;

/// <summary>
/// Plans (and, once an operator confirms, applies) the DDL a table mapping's Setup card previews —
/// see architecture/implementation/todo/phase-025-database-provisioning.md. Follows
/// <see cref="MetadataService"/>/<see cref="DriverConnectionFactory"/> for connection and credential
/// handling: opens what it needs, closes it, no data movement.
/// <para>
/// Both plans come from the same code path <see cref="DbDataSync.TaskRunner.RunExecutor"/>'s automatic
/// <c>CreateTargetTableIfMissing</c> uses (<see cref="ProvisioningColumnBuilder"/>,
/// <see cref="CreateTargetTablePlanner"/>), so the DDL an operator previews here is the same DDL a run
/// would generate unattended — never a second implementation that could quietly disagree.
/// </para>
/// </summary>
public sealed class ProvisioningService(
    ConfigRepository configRepository, IConnectionFactory connections, CurrentUser currentUser,
    MappingColumnReader columnReader, DriverRegistry driverRegistry)
{
    public async Task<ProvisioningPlanReport> GetPlansAsync(
        string replicationName, string mappingName, CancellationToken cancellationToken)
    {
        var (task, mapping, source, target) = LoadMapping(replicationName, mappingName);

        // One source connection, used for both the change-capture plan and (inside PlanTargetAsync)
        // reading the source's columns — the redundant second source connection this used to open per
        // GET is exactly the cost phase 105 §5 asks to stop paying, and it disappears here for free
        // once both planners take a connection rather than opening their own.
        var (sourceConnection, sourceDriver) = await connections.OpenAsync(source.ConnectionName, cancellationToken);
        try
        {
            var sourcePlan = await PlanEnableSourceChangeCaptureAsync(
                task, mapping, source, sourceConnection, sourceDriver, cancellationToken);

            var (targetConnection, targetDriver) = await connections.OpenAsync(target.ConnectionName, cancellationToken);
            try
            {
                var targetPlan = await PlanTargetAsync(
                    task, mapping, source, target, sourceConnection, sourceDriver, targetConnection, targetDriver,
                    cancellationToken);
                return new ProvisioningPlanReport(sourcePlan, targetPlan);
            }
            finally
            {
                await targetConnection.DisposeAsync();
            }
        }
        finally
        {
            await sourceConnection.DisposeAsync();
        }
    }

    public async Task<ApplyResult> ApplyAsync(
        string replicationName, string mappingName, string action, CancellationToken cancellationToken)
    {
        var (task, mapping, source, target) = LoadMapping(replicationName, mappingName);

        if (action == ProvisioningActions.EnableSourceChangeCapture)
        {
            var (sourceConnection, sourceDriver) = await connections.OpenAsync(source.ConnectionName, cancellationToken);
            try
            {
                var plan = await PlanEnableSourceChangeCaptureAsync(
                    task, mapping, source, sourceConnection, sourceDriver, cancellationToken);
                var (results, finalState) = await RunStepsAsync(sourceConnection, plan, cancellationToken);

                if (finalState == ProvisioningState.Satisfied)
                    MarkRenamesApplied(replicationName, mapping);

                return new ApplyResult(results, finalState);
            }
            finally
            {
                await sourceConnection.DisposeAsync();
            }
        }

        if (action is ProvisioningActions.CreateTargetTable or ProvisioningActions.AlterTargetTable)
        {
            var (sourceConnection, sourceDriver) = await connections.OpenAsync(source.ConnectionName, cancellationToken);
            try
            {
                var (targetConnection, targetDriver) = await connections.OpenAsync(target.ConnectionName, cancellationToken);
                try
                {
                    var plan = await PlanTargetAsync(
                        task, mapping, source, target, sourceConnection, sourceDriver, targetConnection, targetDriver,
                        cancellationToken);
                    var (results, finalState) = await RunStepsAsync(targetConnection, plan, cancellationToken);

                    if (finalState == ProvisioningState.Satisfied)
                    {
                        MarkRenamesApplied(replicationName, mapping);

                        // Whoever creates the table records what it created — phase 94's principle,
                        // applied to the other path that creates one.
                        await CacheTargetColumnsAsync(replicationName, mapping.Name, target, cancellationToken);
                    }

                    return new ApplyResult(results, finalState);
                }
                finally
                {
                    await targetConnection.DisposeAsync();
                }
            }
            finally
            {
                await sourceConnection.DisposeAsync();
            }
        }

        throw new ConfigValidationException($"Unknown provisioning action '{action}'.");
    }

    /// <summary>
    /// Runs a plan's steps in order against the connection they were planned for, stopping at the first
    /// one that fails. Shared by the per-mapping Apply above and the replication-wide batch below, so
    /// "what happens when a step fails" is answered once.
    /// </summary>
    private static async Task<(IReadOnlyList<ApplyStepResult> Results, ProvisioningState State)> RunStepsAsync(
        DbConnection connection, ProvisioningPlan plan, CancellationToken cancellationToken)
    {
        var results = new List<ApplyStepResult>();
        foreach (var step in plan.Steps)
        {
            var started = Stopwatch.GetTimestamp();
            try
            {
                using var cmd = connection.CreateTimedCommand();
                cmd.CommandText = step.CommandText;
                await cmd.ExecuteNonQueryAsync(cancellationToken);
                results.Add(new ApplyStepResult(step.Title, true, null, Stopwatch.GetElapsedTime(started).TotalMilliseconds));
            }
            catch (DbException ex)
            {
                // The enterprise-normal path: a least-privileged connection cannot run ALTER
                // DATABASE, and that permission error is the expected outcome, not a defect — it's
                // surfaced intact rather than swallowed, and Copy exists for exactly this case.
                results.Add(new ApplyStepResult(step.Title, false, ex.Message, Stopwatch.GetElapsedTime(started).TotalMilliseconds));
                break;
            }
        }

        var finalState = results.Count > 0 && results.All(r => r.Succeeded) ? ProvisioningState.Satisfied : plan.State;
        return (results, finalState);
    }

    /// <summary>
    /// Every table mapping's provisioning, aggregated — phase 105. Grouped by
    /// <c>(connectionName, database)</c> (one script per server; a replication's source and target are
    /// usually different ones), deduplicated by statement within a group (so N mappings sharing a
    /// source database do not repeat its <c>ALTER DATABASE</c> N times — see phase 25 §4's design,
    /// which this deliberately revisits), and ordered <see cref="ProvisioningStepScope.Database"/>
    /// before <see cref="ProvisioningStepScope.Table"/> within each group.
    /// <para>
    /// Uses the exact same <see cref="PlanEnableSourceChangeCaptureAsync"/> and <see cref="PlanTargetAsync"/>
    /// the per-mapping Setup card calls, so this page and that card can never disagree about what a
    /// mapping needs — only how connections are acquired differs, through <see cref="ConnectionCache"/>,
    /// which opens one connection per distinct endpoint across every mapping rather than one pair per
    /// mapping.
    /// </para>
    /// <para>
    /// A mapping that cannot be planned at all — not 1:1, an endpoint that fails to resolve, a column
    /// mapping referencing a column that is not there — is excluded with its reason rather than
    /// aborting the whole page; so is a mapping whose plan comes back
    /// <see cref="ProvisioningState.Unsupported"/> or <see cref="ProvisioningState.Unknown"/>. A script
    /// covering most of a replication's mappings that looks complete is worse than one that says what
    /// it left out.
    /// </para>
    /// </summary>
    public async Task<ReplicationProvisioningPlan> GetReplicationPlanAsync(
        string replicationName, CancellationToken cancellationToken)
    {
        var task = configRepository.LoadReplicationTask(replicationName);
        var mappingNames = configRepository.ListTableMappings(replicationName);

        var builder = new ReplicationPlanBuilder();
        await using var cache = new ConnectionCache(connections);

        foreach (var mappingName in mappingNames)
        {
            TableMappingConfig mapping;
            SourceTableRef source;
            TableRef target;
            try
            {
                mapping = configRepository.LoadTableMapping(replicationName, mappingName);
                if (mapping.Sources.Count != 1 || mapping.Targets.Count != 1)
                {
                    builder.Exclude(mappingName, null,
                        $"Has {mapping.Sources.Count} source(s) and {mapping.Targets.Count} target(s) — " +
                        "provisioning only plans 1:1 mappings.");
                    continue;
                }

                source = EndpointResolution.ResolveSource(task, mapping.Sources[0]);
                target = EndpointResolution.ResolveTarget(task, mapping.Targets[0]);
            }
            catch (ConfigValidationException ex)
            {
                builder.Exclude(mappingName, null, ex.Message);
                continue;
            }

            try
            {
                var (sourceConnection, sourceDriver) =
                    await cache.GetAsync(source.ConnectionName, source.Database, cancellationToken);
                var sourcePlan = await PlanEnableSourceChangeCaptureAsync(
                    task, mapping, source, sourceConnection, sourceDriver, cancellationToken);
                builder.Absorb(
                    sourcePlan, source.ConnectionName, source.Database, ProvisioningEndpointSide.Source,
                    mappingName, automatic: false);

                var (targetConnection, targetDriver) =
                    await cache.GetAsync(target.ConnectionName, target.Database, cancellationToken);
                var targetPlan = await PlanTargetAsync(
                    task, mapping, source, target, sourceConnection, sourceDriver, targetConnection, targetDriver,
                    cancellationToken);

                // Whether RunExecutor would run this exact statement unattended on the pass after this
                // one — the two target auto-flags, resolved for this mapping specifically (phase 68's
                // per-mapping override applies here exactly as it does to a live run).
                var automatic =
                    (targetPlan.Action == ProvisioningActions.CreateTargetTable
                        && ProvisioningResolution.CreateTargetTableIfMissing(task, mapping))
                    || (targetPlan.Action == ProvisioningActions.AlterTargetTable
                        && ProvisioningResolution.AlterTargetTableColumns(task, mapping));
                builder.Absorb(
                    targetPlan, target.ConnectionName, target.Database, ProvisioningEndpointSide.Target,
                    mappingName, automatic);
            }
            catch (Exception ex) when (ex is InvalidOperationException or ConfigValidationException)
            {
                // A source that cannot be read yet, a column mapping naming a column that is not there
                // — real, mapping-specific reasons a plan cannot be computed. One bad mapping does not
                // get to take the whole page down.
                builder.Exclude(mappingName, null, ex.Message);
            }
        }

        return builder.Build();
    }

    /// <summary>
    /// Applies a subset of a freshly-recomputed replication-wide plan — phase 105 §4. Re-plans first,
    /// exactly as the per-mapping Apply does and for the same reason: applying stale DDL is exactly the
    /// failure the preview exists to prevent, and re-planning is also what makes two operators applying
    /// at once safe — the second one's plan is simply shorter, and any id it selected that the first
    /// already satisfied comes back <see cref="ProvisioningStepOutcome.NoLongerNeeded"/> rather than a
    /// conflict.
    /// <para>
    /// Not a transaction — cannot be, across servers. Stops at the first failure; everything selected
    /// after it comes back <see cref="ProvisioningStepOutcome.NotAttempted"/>, because a later step
    /// commonly depends on an earlier one and running it anyway would bury the real error under a
    /// cascade of consequences it caused.
    /// </para>
    /// </summary>
    public async Task<ReplicationProvisioningApplyResult> ApplyReplicationPlanAsync(
        string replicationName, IReadOnlyList<string> stepIds, CancellationToken cancellationToken)
    {
        var plan = await GetReplicationPlanAsync(replicationName, cancellationToken);
        var selected = new HashSet<string>(stepIds);
        var matched = new HashSet<string>();
        var results = new List<ReplicationProvisioningStepResult>();
        var stopped = false;

        await using var cache = new ConnectionCache(connections);
        foreach (var group in plan.Groups)
        {
            var selectedInGroup = group.Steps.Where(s => selected.Contains(s.Id)).ToList();
            if (selectedInGroup.Count == 0)
                continue;

            // The one declared dependency rule (phase 105 §3): a selected Table-scope step warns, but
            // still runs, when this group's own Database-scope step exists in the fresh plan and was
            // not selected. A Database step absent from the fresh plan means its prerequisite is
            // already satisfied, which is not a warning at all.
            var unselectedDatabaseSteps = group.Steps
                .Where(s => s.Scope == ProvisioningStepScope.Database && !selected.Contains(s.Id))
                .ToList();

            DbConnection? connection = stopped
                ? null
                : (await cache.GetAsync(group.ConnectionName, group.Database, cancellationToken)).Connection;

            foreach (var step in group.Steps)
            {
                if (!selected.Contains(step.Id))
                    continue;
                matched.Add(step.Id);

                if (stopped)
                {
                    results.Add(new ReplicationProvisioningStepResult(
                        step.Id, step.Title, ProvisioningStepOutcome.NotAttempted, null, null, 0));
                    continue;
                }

                var warning = step.Scope == ProvisioningStepScope.Table && unselectedDatabaseSteps.Count > 0
                    ? "This step's database-level prerequisite was not selected. If it has not already " +
                      "been applied out of band, this statement may fail."
                    : null;

                var started = Stopwatch.GetTimestamp();
                try
                {
                    using var cmd = connection!.CreateTimedCommand();
                    cmd.CommandText = step.CommandText;
                    await cmd.ExecuteNonQueryAsync(cancellationToken);
                    results.Add(new ReplicationProvisioningStepResult(
                        step.Id, step.Title, ProvisioningStepOutcome.Applied, null, warning,
                        Stopwatch.GetElapsedTime(started).TotalMilliseconds));
                }
                catch (DbException ex)
                {
                    results.Add(new ReplicationProvisioningStepResult(
                        step.Id, step.Title, ProvisioningStepOutcome.Failed, ex.Message, warning,
                        Stopwatch.GetElapsedTime(started).TotalMilliseconds));
                    stopped = true;
                }
            }
        }

        // An id the operator selected that the fresh plan does not contain at all — someone else
        // already satisfied it between preview and Apply, which is the normal outcome on a database
        // this replication shares rather than a failure (phase 105 §2).
        foreach (var id in stepIds)
            if (!matched.Contains(id))
                results.Add(new ReplicationProvisioningStepResult(
                    id, id, ProvisioningStepOutcome.NoLongerNeeded, null, null, 0));

        // Whoever creates or alters a target's shape records what it created — the same rule the
        // per-mapping Apply already follows (phase 94/97's ApplyAsync above). Without this, a mapping
        // provisioned only through this aggregate page would create its target correctly and then fail
        // its own first run against an uncached target shape, exactly the bug phase 97 fixed for the
        // Setup card's own Apply button.
        //
        // A mapping earns the cache refresh when every Table-scope, Target-side step it contributed to
        // this call actually applied — never for a step that is merely absent from the fresh plan
        // (NoLongerNeeded), since that case has no ContributingMappings to read here at all: the step
        // vanished from the plan before this loop ever saw it.
        var outcomeById = results.ToDictionary(r => r.Id, r => r.Outcome);
        var provisionedMappings = plan.Groups
            .Where(g => g.Side == ProvisioningEndpointSide.Target)
            .SelectMany(g => g.Steps)
            .Where(s => s.Scope == ProvisioningStepScope.Table && selected.Contains(s.Id))
            .SelectMany(s => s.ContributingMappings.Select(mapping => (Mapping: mapping, s.Id)))
            .GroupBy(x => x.Mapping);

        foreach (var group in provisionedMappings)
        {
            var allApplied = group.All(x =>
                outcomeById.TryGetValue(x.Id, out var outcome) && outcome == ProvisioningStepOutcome.Applied);
            if (!allApplied)
                continue;

            var (_, mapping, _, target) = LoadMapping(replicationName, group.Key);
            MarkRenamesApplied(replicationName, mapping);
            await CacheTargetColumnsAsync(replicationName, group.Key, target, cancellationToken);
        }

        return new ReplicationProvisioningApplyResult(results);
    }

    /// <summary>
    /// Opens at most one connection per distinct <c>(connectionName, database)</c> pair for the whole
    /// call it is used within, and disposes every one of them together — phase 105 §5's "forty mappings
    /// must not mean eighty connections". A test substitutes a counting <see cref="IConnectionFactory"/>
    /// here to pin the guarantee, since it is otherwise invisible in behaviour and would regress
    /// silently.
    /// </summary>
    private sealed class ConnectionCache(IConnectionFactory connections) : IAsyncDisposable
    {
        private readonly Dictionary<(string ConnectionName, string Database), (DbConnection Connection, IDriver Driver)>
            _opened = [];

        public async Task<(DbConnection Connection, IDriver Driver)> GetAsync(
            string connectionName, string database, CancellationToken cancellationToken)
        {
            var key = (connectionName, database);
            if (_opened.TryGetValue(key, out var existing))
                return existing;

            var opened = await connections.OpenAsync(connectionName, cancellationToken);
            _opened[key] = opened;
            return opened;
        }

        public async ValueTask DisposeAsync()
        {
            foreach (var (connection, _) in _opened.Values)
                await connection.DisposeAsync();
        }
    }

    /// <summary>
    /// Merges every mapping's per-side plan into <see cref="ReplicationProvisioningPlan"/>'s groups —
    /// the dedup-by-statement, group-by-endpoint, order-by-scope machinery phase 105 §1 asks for, kept
    /// off <see cref="GetReplicationPlanAsync"/> itself so that method reads as the planning loop it is.
    /// </summary>
    private sealed class ReplicationPlanBuilder
    {
        private sealed class StepAccumulator(
            string id, string title, string commandText, string? rationale, ProvisioningStepScope scope,
            ProvisioningEndpointSide side)
        {
            public string Id { get; } = id;
            public string Title { get; } = title;
            public string CommandText { get; } = commandText;
            public string? Rationale { get; } = rationale;
            public ProvisioningStepScope Scope { get; } = scope;
            public ProvisioningEndpointSide Side { get; } = side;
            public bool Automatic { get; set; }
            public List<string> ContributingMappings { get; } = [];
        }

        private readonly Dictionary<string, StepAccumulator> _stepsById = [];
        private readonly List<(string ConnectionName, string Database, ProvisioningEndpointSide Side)> _groupOrder = [];
        private readonly Dictionary<(string, string, ProvisioningEndpointSide), List<StepAccumulator>> _groups = [];
        private readonly List<ExcludedMapping> _excluded = [];

        public void Exclude(string mappingName, ProvisioningEndpointSide? side, string reason) =>
            _excluded.Add(new ExcludedMapping(mappingName, side, reason));

        /// <summary>
        /// Folds one mapping's one-side plan in. Deduping happens here, keyed by
        /// <see cref="ProvisioningStepId.Compute"/> — the same key Apply matches a selection back
        /// against, so two mappings that would run the identical statement against the identical
        /// endpoint collapse to one row naming both, rather than one row per mapping.
        /// </summary>
        public void Absorb(
            ProvisioningPlan plan, string connectionName, string database, ProvisioningEndpointSide side,
            string mappingName, bool automatic)
        {
            if (plan.State is ProvisioningState.Unsupported or ProvisioningState.Unknown)
            {
                var reason = plan.Warnings.Count > 0
                    ? string.Join(" ", plan.Warnings)
                    : $"'{plan.Action}' is not supported for this connection.";
                Exclude(mappingName, side, reason);
                return;
            }

            foreach (var step in plan.Steps)
            {
                var id = ProvisioningStepId.Compute(connectionName, database, step.Scope, step.CommandText);
                if (!_stepsById.TryGetValue(id, out var accumulator))
                {
                    accumulator = new StepAccumulator(id, step.Title, step.CommandText, step.Rationale, step.Scope, side);
                    _stepsById[id] = accumulator;

                    var key = (connectionName, database, side);
                    if (!_groups.TryGetValue(key, out var steps))
                    {
                        steps = [];
                        _groups[key] = steps;
                        _groupOrder.Add(key);
                    }
                    steps.Add(accumulator);
                }

                if (!accumulator.ContributingMappings.Contains(mappingName))
                    accumulator.ContributingMappings.Add(mappingName);
                accumulator.Automatic |= automatic;
            }
        }

        public ReplicationProvisioningPlan Build()
        {
            var groups = _groupOrder.Select(key =>
            {
                var (connectionName, database, side) = key;
                // OrderBy is a stable sort (documented LINQ behaviour), so Database-scope steps move
                // ahead of Table-scope ones without disturbing first-appearance order within either
                // scope — phase 105 §1's ordering rule, in one line rather than a custom comparer.
                var steps = _groups[key]
                    .OrderBy(s => s.Scope == ProvisioningStepScope.Database ? 0 : 1)
                    .Select(s => new ReplicationProvisioningStep(
                        s.Id, s.Title, s.CommandText, s.Rationale, s.Scope, s.Side, s.Automatic,
                        s.ContributingMappings))
                    .ToList();
                return new ReplicationProvisioningGroup(connectionName, database, side, steps);
            }).ToList();

            return new ReplicationProvisioningPlan(groups, _excluded);
        }
    }

    /// <summary>
    /// What each of the source's columns would become on the target, if nobody overrode it.
    /// <para>
    /// The column mapping editor needs this because the canonical type system lives entirely on this
    /// side: the SPA has no way to work out that a SQL Server <c>nvarchar(50)</c> lands as a Postgres
    /// <c>varchar(50)</c>, and an editor that showed the source's type and an empty box would be
    /// asking the operator to do that translation in their head. Every source column is answered, not
    /// just the mapped ones, so adding a mapping shows its type immediately.
    /// </para>
    /// <para>
    /// Deliberately the same translation <see cref="PlanTargetAsync"/> uses rather than a second one,
    /// so what the editor shows and what the DDL says cannot drift apart.
    /// </para>
    /// </summary>
    public async Task<IReadOnlyList<InferredColumnType>> GetInferredTargetTypesAsync(
        string replicationName, string mappingName, CancellationToken cancellationToken)
    {
        var (_, _, source, target) = LoadMapping(replicationName, mappingName);

        var (sourceConnection, sourceDriver) = await connections.OpenAsync(source.ConnectionName, cancellationToken);
        IReadOnlyList<ColumnMetadata> sourceColumns;
        try
        {
            sourceColumns = await sourceDriver.ListColumnsAsync(
                sourceConnection, source.Database, source.Schema, source.Table, cancellationToken);
        }
        finally
        {
            await sourceConnection.DisposeAsync();
        }

        var sourceDialect = ResolveDialect(sourceDriver);
        var targetDialect = ResolveDialect(
            driverRegistry.Get(configRepository.LoadConnection(target.ConnectionName).DriverType));

        return [.. sourceColumns.Select(column => Infer(sourceDialect, targetDialect, column))];
    }

    /// <summary>
    /// What the SCD Type 2 writer's natural key would be for this mapping if nobody stated one — the
    /// source's primary key, said in the target's column names.
    /// <para>
    /// Beside <see cref="GetInferredTargetTypesAsync"/> because it is the same job: read the source's
    /// <see cref="ColumnMetadata"/> and answer a per-mapping question the SPA has no way to work out
    /// for itself. And through the same <see cref="NaturalKeyDerivation"/> helper
    /// <c>RunExecutor</c> injects with at run time, so what the Pipeline tab says would happen and what
    /// happens cannot quietly disagree.
    /// </para>
    /// <para>
    /// Reports why it derived nothing rather than answering with an empty list and no explanation:
    /// "there is no natural key here" and "you have not looked" are different answers, and only one of
    /// them means the operator has to type one.
    /// </para>
    /// </summary>
    public async Task<InferredNaturalKey> GetInferredNaturalKeyAsync(
        string replicationName, string mappingName, CancellationToken cancellationToken)
    {
        var (_, mapping, source, _) = LoadMapping(replicationName, mappingName);

        var (sourceConnection, sourceDriver) = await connections.OpenAsync(source.ConnectionName, cancellationToken);
        IReadOnlyList<ColumnMetadata> sourceColumns;
        try
        {
            sourceColumns = await sourceDriver.ListColumnsAsync(
                sourceConnection, source.Database, source.Schema, source.Table, cancellationToken);
        }
        finally
        {
            await sourceConnection.DisposeAsync();
        }

        var derived = NaturalKeyDerivation.Derive(sourceColumns, mapping.ColumnMappings);
        if (derived.Count > 0)
            return new InferredNaturalKey(derived, null);

        var keyColumns = sourceColumns.Where(c => c.IsPrimaryKey).Select(c => c.Name).ToList();
        var unmapped = keyColumns
            .Where(k => !mapping.ColumnMappings.Any(m => string.Equals(m.SourceColumn, k, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        return new InferredNaturalKey([], keyColumns.Count == 0
            ? $"'{source.Schema}.{source.Table}' has no primary key, so there is nothing to derive a " +
              "natural key from. Enter one below."
            : $"'{source.Schema}.{source.Table}'s primary key includes {string.Join(", ", unmapped.Select(u => $"'{u}'"))}, " +
              "which this mapping does not carry across. Map those columns, or enter a natural key below.");
    }

    /// <summary>
    /// One column's inference, or a null <c>TargetType</c> with the reason.
    /// <para>
    /// A type with no cross-engine equivalent is reported as such rather than thrown, because one
    /// unmappable column should not stop the editor showing the other forty — and the operator's
    /// answer to it is to type a target type of their own, which is exactly what the editor offers.
    /// </para>
    /// </summary>
    private static InferredColumnType Infer(SqlDialect sourceDialect, SqlDialect targetDialect, ColumnMetadata column)
    {
        try
        {
            var canonical = sourceDialect.ToCanonicalType(column.NativeType);
            if (canonical.Kind == CanonicalTypeKind.Unmappable)
                return new InferredColumnType(column.Name, column.NativeType, null, null,
                    $"'{column.NativeType}' has no cross-engine equivalent — set a target type by hand.");

            var rendered = targetDialect.RenderColumnType(canonical);
            return new InferredColumnType(column.Name, column.NativeType, rendered.Sql, rendered.Fidelity, null);
        }
        catch (Exception ex) when (ex is NotSupportedException or InvalidOperationException or FormatException)
        {
            return new InferredColumnType(column.Name, column.NativeType, null, null, ex.Message);
        }
    }

    private (ReplicationTaskConfig Task, TableMappingConfig Mapping, SourceTableRef Source, TableRef Target) LoadMapping(
        string replicationName, string mappingName)
    {
        var task = configRepository.LoadReplicationTask(replicationName);
        var mapping = configRepository.LoadTableMapping(replicationName, mappingName);
        if (mapping.Sources.Count != 1 || mapping.Targets.Count != 1)
            throw new ConfigValidationException(
                $"Table mapping '{mapping.Name}' has {mapping.Sources.Count} source(s) and " +
                $"{mapping.Targets.Count} target(s) — provisioning only plans 1:1 mappings.");

        var source = EndpointResolution.ResolveSource(task, mapping.Sources[0]);
        var target = EndpointResolution.ResolveTarget(task, mapping.Targets[0]);
        return (task, mapping, source, target);
    }

    /// <summary>
    /// Plans <see cref="ProvisioningActions.EnableSourceChangeCapture"/> against an already-open source
    /// connection.
    /// <para>
    /// Static, and taking the connection rather than opening one, since phase 105: the per-mapping
    /// Setup card and the replication-wide aggregate must plan from the exact same code, and the only
    /// way to guarantee that is for neither of them to own connection acquisition. See
    /// <see cref="GetReplicationPlanAsync"/>'s <see cref="ConnectionCache"/> for the caller that opens
    /// one connection per distinct source instead of one per mapping.
    /// </para>
    /// </summary>
    private static async Task<ProvisioningPlan> PlanEnableSourceChangeCaptureAsync(
        ReplicationTaskConfig task, TableMappingConfig mapping, SourceTableRef source,
        DbConnection sourceConnection, IDriver sourceDriver, CancellationToken cancellationToken)
    {
        if (sourceDriver is not IProvisioner provisioner)
            return Unsupported(ProvisioningActions.EnableSourceChangeCapture, sourceDriver.DriverType);

        // This mapping's effective reader — enabling change capture is planned for the reader that
        // will actually read this table, which a mapping may override (phase 68).
        var reader = PipelineResolution.Reader(task, mapping);
        var request = new ProvisioningRequest(
            ProvisioningActions.EnableSourceChangeCapture, source, reader.Kind, reader.Options, []);
        return await provisioner.PlanAsync(sourceConnection, request, cancellationToken);
    }

    /// <summary>
    /// The target side's one plan.
    /// <para>
    /// "Create it" and "fix it" are two answers to one question — make the target fit — and which one
    /// applies is decided by whether the table exists, not by the operator. So this asks the driver for
    /// whichever is applicable and returns a single plan, which is why the card shows one Target panel
    /// rather than two that are never both relevant.
    /// </para>
    /// <para>
    /// **Not gated by the provisioning settings.** Those govern what a *pass* may do unattended; this
    /// card is where a person presses Apply deliberately, and refusing to show them what the change
    /// would be because automation is switched off would answer a question nobody asked.
    /// </para>
    /// </summary>
    /// <summary>
    /// Plans the target's one applicable action (create or alter) against already-open source and
    /// target connections. Static for the same reason as <see cref="PlanEnableSourceChangeCaptureAsync"/>
    /// beside it — see that method's doc.
    /// </summary>
    private static async Task<ProvisioningPlan> PlanTargetAsync(
        ReplicationTaskConfig task, TableMappingConfig mapping, SourceTableRef source, TableRef target,
        DbConnection sourceConnection, IDriver sourceDriver, DbConnection targetConnection, IDriver targetDriver,
        CancellationToken cancellationToken)
    {
        var sourceColumns = await sourceDriver.ListColumnsAsync(
            sourceConnection, source.Database, source.Schema, source.Table, cancellationToken);

        if (targetDriver is not IProvisioner provisioner)
            return Unsupported(ProvisioningActions.CreateTargetTable, targetDriver.DriverType);

        var (columns, identityWarnings) = ProvisioningColumnBuilder.Build(
            ResolveDialect(sourceDriver), sourceColumns, mapping.ColumnMappings);

        // Extended with whatever the configured writer needs beyond the mapped columns — a snapshot's
        // marker, an SCD Type 2 target's version key and validity range. The same list the create and
        // alter planners already work from, rather than a second provisioning path.
        // The mapping's effective writer, not the replication's: a mapping that overrides its way onto
        // Scd2 needs the version columns in this plan, and the run that creates the table unattended
        // resolves the same way (see RunExecutor.EnsureTargetTableProvisionedAsync).
        var writerKind = PipelineResolution.Writer(task, mapping).Kind;
        var provisioned = HistorizedProvisioning.Extend(columns, writerKind);

        ProvisioningRequest Request(string action) => new(
            action, target, ReaderKind: null, ReaderOptions: new Dictionary<string, string>(), provisioned,
            writerKind);

        // Create first: its plan comes back Satisfied when the table already exists, which is exactly
        // when the alter plan is the one with something to say. The two are mutually exclusive, which
        // is why one panel is the right shape rather than two that are never both relevant.
        var create = await provisioner.PlanAsync(
            targetConnection, Request(ProvisioningActions.CreateTargetTable), cancellationToken);

        var plan = create.State is ProvisioningState.Missing or ProvisioningState.Unsupported
            ? create
            : await provisioner.PlanAsync(
                targetConnection, Request(ProvisioningActions.AlterTargetTable), cancellationToken);

        return plan with { Warnings = [.. identityWarnings, .. plan.Warnings] };
    }

    /// <summary>
    /// Records that the target has caught up with a mapping's pending renames.
    /// <para>
    /// Bookkeeping, not a latch. The planner reads the target's actual columns, so a step left
    /// unapplied because this write did not happen plans nothing next time round — the rename has
    /// already been done and the old name is gone. What it buys is a history that says what happened,
    /// rather than one that says a rename is still outstanding forever.
    /// </para>
    /// <para>
    /// Only after every step succeeded. A part-applied plan is exactly when the flags would be a lie,
    /// and re-planning against the target tells the truth for free.
    /// </para>
    /// </summary>
    private void MarkRenamesApplied(string replicationName, TableMappingConfig mapping)
    {
        var pending = mapping.ColumnMappings.SelectMany(c => c.Renames).Where(r => !r.Applied).ToList();
        if (pending.Count == 0)
            return;

        foreach (var rename in pending)
            rename.Applied = true;

        configRepository.SaveTableMapping(replicationName, mapping, currentUser.Author);
    }

    /// <summary>
    /// Puts the target's shape into the mapping's phase-90 cache, now that Apply has vouched for it.
    /// <para>
    /// Phase 91 made writers read that cache and nowhere else, and phase 94 gave the *unattended*
    /// provisioning path a way to fill it. The Apply button runs the identical DDL through the
    /// identical planner and filled nothing — so the deliberate flow, previewing the DDL before
    /// running it, created its target correctly and then failed its own first run on an empty cache.
    /// See phase 97.
    /// </para>
    /// <para>
    /// **Read back from the catalog, not taken from the plan** — the same three reasons
    /// <c>RunExecutor.CacheProvisionedTargetColumnsAsync</c> gives: a <c>ProvisioningColumn</c> carries
    /// a canonical type rather than the native one the target rendered it to, carries no identity
    /// flag, and on the alter path names only the mapped columns. Through
    /// <see cref="MappingColumnReader"/> specifically, so what Apply caches is the same answer Refresh
    /// metadata would have given — one introspection path, so a cache cannot disagree with the editor
    /// beside it.
    /// </para>
    /// <para>
    /// Re-loads the mapping rather than writing back the copy this request opened with: that copy is
    /// as old as the start of the Apply, and <see cref="MarkRenamesApplied"/> may already have saved
    /// it. This write is about one field, so it is applied to one field of the current file — the same
    /// rule <c>LocalRunnerConfig</c> follows.
    /// </para>
    /// </summary>
    private async Task CacheTargetColumnsAsync(
        string replicationName, string mappingName, TableRef target, CancellationToken cancellationToken)
    {
        var read = await columnReader.ReadAsync(target, cancellationToken);

        // Nothing to record, on either count. A target that could not be read is not a target with no
        // columns, and an empty capture is a state phase 91 acts on — writing one would replace a
        // usable picture with a worse answer than the one that is there.
        if (read.Shape is not { } shape)
            return;

        var mapping = configRepository.LoadTableMapping(replicationName, mappingName);
        if (CachedColumn.SameShape(mapping.TargetColumns, shape))
            return;

        mapping.TargetColumns = shape;
        // Stamped for the reason a Refresh stamps it: the target really was read just now, and the age
        // on screen is what an operator decides staleness from.
        mapping.ColumnsCapturedUtc = DateTime.UtcNow;

        configRepository.SaveTableMapping(replicationName, mapping, currentUser.Author);
    }

    private static ProvisioningPlan Unsupported(string action, string driverType) =>
        new(action, ProvisioningState.Unknown, [], [$"The '{driverType}' driver does not support provisioning."]);

    /// <summary>
    /// Was a hardcoded per-engine switch here — MsSql/Postgres/DuckDb only, so it threw for MySQL,
    /// Oracle, and any descriptor/YAML-based driver (a JDBC connector included) the moment a mapping
    /// editor opened, regardless of anything the operator had configured. <c>RunExecutor.ResolveDialect</c>
    /// generalized via <see cref="IDialectProvider"/> at phase 29 specifically so a new driver never
    /// has to be remembered here; this one was never migrated to match, and MsSql-only setups never
    /// noticed because they never exercised any other branch. Same shape as
    /// <c>DbDataSync.TaskRunner.RunExecutor.ResolveDialect</c> now, not just documented to match it.
    /// </summary>
    private static SqlDialect ResolveDialect(IDriver driver) =>
        (driver as IDialectProvider)?.Dialect
        ?? throw new InvalidOperationException($"The '{driver.DriverType}' driver does not name a SQL dialect.");
}

public sealed record ProvisioningPlanReport(ProvisioningPlan Source, ProvisioningPlan Target);

/// <param name="Fidelity">What the translation loses or approximates, when it does — the same note
/// the generated DDL carries as a warning, shown next to the type rather than only after a plan.</param>
/// <param name="Problem">Why there is no inferred type, when there isn't. Null and
/// <paramref name="TargetType"/> are never both set.</param>
public sealed record InferredColumnType(
    string SourceColumn, string SourceType, string? TargetType, string? Fidelity, string? Problem);

/// <param name="Columns">Target column names, in the order the source lists them. Empty when nothing
/// could be derived, in which case <paramref name="Problem"/> says why.</param>
/// <param name="Problem">Why there is no derived key, when there isn't. Never set alongside a
/// non-empty <paramref name="Columns"/>.</param>
public sealed record InferredNaturalKey(IReadOnlyList<string> Columns, string? Problem);

public sealed record ApplyStepResult(string Title, bool Succeeded, string? Error, double ElapsedMs);

public sealed record ApplyResult(IReadOnlyList<ApplyStepResult> Steps, ProvisioningState State);

// ---- Phase 105: one provisioning plan for the whole replication -----------------------------------

/// <summary>Which side of a mapping a step or exclusion belongs to — a source connection's DDL and a
/// target connection's DDL are never the same script (phase 105 §1), so every group and every excluded
/// mapping says which one it is.</summary>
public enum ProvisioningEndpointSide
{
    Source,
    Target,
}

/// <summary>
/// A step's id is a hash of what it *is* — never a position — because Apply re-plans from scratch and
/// matches the operator's selection back onto the fresh plan by this id (phase 105 §2). A position-based
/// id would make that match meaningless the instant the plan's shape changed between preview and Apply,
/// which on a page aggregating forty mappings is closer to the rule than the exception.
/// </summary>
public static class ProvisioningStepId
{
    // The ASCII Unit Separator — vanishingly unlikely to appear in a connection name, a database name
    // or generated DDL, so joining the four fields on it cannot make two distinct inputs hash the same.
    private const char SeparatorChar = '';

    public static string Compute(
        string connectionName, string database, ProvisioningStepScope scope, string commandText)
    {
        var input = string.Join(SeparatorChar, connectionName, database, scope, commandText);
        var hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(hash);
    }
}

/// <param name="ContributingMappings">Every table mapping whose plan produced this exact statement
/// against this exact endpoint — a database-scope step commonly names several; a table-scope one
/// commonly names one.</param>
/// <param name="Automatic">True when the target's own <c>CreateTargetTableIfMissing</c> or
/// <c>AlterTargetTableColumnsIfMissingOrChanged</c> flag would apply this exact statement anyway on the
/// next unattended pass, for at least one contributing mapping. Shown and ticked regardless — see phase
/// 105 §3 — because a copied script that quietly omits a step the app *might* do later is a
/// half-configured database to whoever runs it by hand.</param>
public sealed record ReplicationProvisioningStep(
    string Id,
    string Title,
    string CommandText,
    string? Rationale,
    ProvisioningStepScope Scope,
    ProvisioningEndpointSide Side,
    bool Automatic,
    IReadOnlyList<string> ContributingMappings);

/// <summary>One script for one server: every step sharing this connection, database and side, ordered
/// database-scope before table-scope.</summary>
public sealed record ReplicationProvisioningGroup(
    string ConnectionName,
    string Database,
    ProvisioningEndpointSide Side,
    IReadOnlyList<ReplicationProvisioningStep> Steps);

/// <param name="Side">Null when the mapping itself could not be planned at all (not 1:1, an endpoint
/// that does not resolve) — the exclusion is not about either side in particular. Set when one side's
/// plan came back <see cref="ProvisioningState.Unsupported"/> or <see cref="ProvisioningState.Unknown"/>
/// while the other side may still have contributed steps normally.</param>
public sealed record ExcludedMapping(string MappingName, ProvisioningEndpointSide? Side, string Reason);

public sealed record ReplicationProvisioningPlan(
    IReadOnlyList<ReplicationProvisioningGroup> Groups, IReadOnlyList<ExcludedMapping> Excluded);

public enum ProvisioningStepOutcome
{
    Applied,
    Failed,

    /// <summary>The id was not in the freshly-recomputed plan at all — normal on a database this
    /// replication shares, meaning someone else already satisfied it between preview and Apply. Never a
    /// failure (phase 105 §2).</summary>
    NoLongerNeeded,

    /// <summary>Selected, but never reached because an earlier step in the batch failed — phase 105 §4:
    /// the batch is not a transaction and stops at the first failure rather than burying it under a
    /// cascade of consequences.</summary>
    NotAttempted,
}

/// <param name="Warning">Set when this is a <see cref="ProvisioningStepScope.Table"/> step and its
/// group's own <see cref="ProvisioningStepScope.Database"/> step(s) existed in the fresh plan but were
/// not selected — warned, never blocked, because the commonest reason to leave the database statement
/// unticked is that a DBA already ran it out of band (phase 105 §3).</param>
public sealed record ReplicationProvisioningStepResult(
    string Id, string Title, ProvisioningStepOutcome Outcome, string? Error, string? Warning, double ElapsedMs);

public sealed record ReplicationProvisioningApplyResult(IReadOnlyList<ReplicationProvisioningStepResult> Steps);
