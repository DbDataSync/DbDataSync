using System.Data.Common;
using System.Diagnostics;
using DataSync.Api.Auth;
using DataSync.Core.Config;
using DataSync.Core.Git;
using DataSync.Drivers.Abstractions;
using DataSync.Drivers.Generic;
using DataSync.Drivers.MsSql;
using DataSync.Drivers.Postgres;
using DataSync.Core.Sql;

namespace DataSync.Api.Services;

/// <summary>
/// Plans (and, once an operator confirms, applies) the DDL a table mapping's Setup card previews —
/// see architecture/implementation/todo/phase-025-database-provisioning.md. Follows
/// <see cref="MetadataService"/>/<see cref="DriverConnectionFactory"/> for connection and credential
/// handling: opens what it needs, closes it, no data movement.
/// <para>
/// Both plans come from the same code path <see cref="DataSync.TaskRunner.RunExecutor"/>'s automatic
/// <c>CreateTargetTableIfMissing</c> uses (<see cref="ProvisioningColumnBuilder"/>,
/// <see cref="CreateTargetTablePlanner"/>), so the DDL an operator previews here is the same DDL a run
/// would generate unattended — never a second implementation that could quietly disagree.
/// </para>
/// </summary>
public sealed class ProvisioningService(
    ConfigRepository configRepository, DriverConnectionFactory connections, CurrentUser currentUser)
{
    public async Task<ProvisioningPlanReport> GetPlansAsync(
        string replicationName, string mappingName, CancellationToken cancellationToken)
    {
        var (task, mapping, source, target) = LoadMapping(replicationName, mappingName);

        var (sourceConnection, sourcePlan) = await PlanEnableSourceChangeCaptureAsync(task, mapping, source, cancellationToken);
        if (sourceConnection is not null)
            await sourceConnection.DisposeAsync();

        var (targetConnection, targetPlan) = await PlanTargetAsync(task, mapping, source, target, cancellationToken);
        if (targetConnection is not null)
            await targetConnection.DisposeAsync();

        return new ProvisioningPlanReport(sourcePlan, targetPlan);
    }

    public async Task<ApplyResult> ApplyAsync(
        string replicationName, string mappingName, string action, CancellationToken cancellationToken)
    {
        var (task, mapping, source, target) = LoadMapping(replicationName, mappingName);

        var (connection, plan) = action switch
        {
            ProvisioningActions.EnableSourceChangeCapture => await PlanEnableSourceChangeCaptureAsync(task, mapping, source, cancellationToken),
            ProvisioningActions.CreateTargetTable or ProvisioningActions.AlterTargetTable =>
                await PlanTargetAsync(task, mapping, source, target, cancellationToken),
            _ => throw new ConfigValidationException($"Unknown provisioning action '{action}'."),
        };

        if (connection is null)
            return new ApplyResult([], plan.State);

        try
        {
            var results = new List<ApplyStepResult>();
            foreach (var step in plan.Steps)
            {
                var started = Stopwatch.GetTimestamp();
                try
                {
                    using var cmd = connection.CreateCommand();
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

            if (finalState == ProvisioningState.Satisfied)
                MarkRenamesApplied(replicationName, mapping);

            return new ApplyResult(results, finalState);
        }
        finally
        {
            await connection.DisposeAsync();
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

        var sourceDialect = ResolveDialect(sourceDriver.DriverType);
        var targetDialect = ResolveDialect(configRepository.LoadConnection(target.ConnectionName).DriverType);

        return [.. sourceColumns.Select(column => Infer(sourceDialect, targetDialect, column))];
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

    private async Task<(DbConnection? Connection, ProvisioningPlan Plan)> PlanEnableSourceChangeCaptureAsync(
        ReplicationTaskConfig task, TableMappingConfig mapping, SourceTableRef source,
        CancellationToken cancellationToken)
    {
        var (connection, driver) = await connections.OpenAsync(source.ConnectionName, cancellationToken);
        if (driver is not IProvisioner provisioner)
        {
            await connection.DisposeAsync();
            return (null, Unsupported(ProvisioningActions.EnableSourceChangeCapture, driver.DriverType));
        }

        // This mapping's effective reader — enabling change capture is planned for the reader that
        // will actually read this table, which a mapping may override (phase 68).
        var reader = PipelineResolution.Reader(task, mapping);
        var request = new ProvisioningRequest(
            ProvisioningActions.EnableSourceChangeCapture, source, reader.Kind, reader.Options, []);
        var plan = await provisioner.PlanAsync(connection, request, cancellationToken);
        return (connection, plan);
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
    private async Task<(DbConnection? Connection, ProvisioningPlan Plan)> PlanTargetAsync(
        ReplicationTaskConfig task, TableMappingConfig mapping, SourceTableRef source, TableRef target,
        CancellationToken cancellationToken)
    {
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

        var (targetConnection, targetDriver) = await connections.OpenAsync(target.ConnectionName, cancellationToken);
        if (targetDriver is not IProvisioner provisioner)
        {
            await targetConnection.DisposeAsync();
            return (null, Unsupported(ProvisioningActions.CreateTargetTable, targetDriver.DriverType));
        }

        var (columns, identityWarnings) = ProvisioningColumnBuilder.Build(
            ResolveDialect(sourceDriver.DriverType), sourceColumns, mapping.ColumnMappings);

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

        return (targetConnection, plan with { Warnings = [.. identityWarnings, .. plan.Warnings] });
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

    private static ProvisioningPlan Unsupported(string action, ConnectionDriverType driverType) =>
        new(action, ProvisioningState.Unknown, [], [$"The '{driverType}' driver does not support provisioning."]);

    /// <summary>Same shape as <c>DataSync.TaskRunner.RunExecutor.ResolveDialect</c> — the one place
    /// this layer needs a concrete <see cref="SqlDialect"/> for an engine it isn't otherwise driving,
    /// to translate the *source's* native column types into canonical form.</summary>
    private static SqlDialect ResolveDialect(ConnectionDriverType driverType) => driverType switch
    {
        ConnectionDriverType.MsSql => MsSqlDialect.Instance,
        ConnectionDriverType.Postgres => PostgresDialect.Instance,
        _ => throw new InvalidOperationException($"No SqlDialect is registered for driver type '{driverType}'."),
    };
}

public sealed record ProvisioningPlanReport(ProvisioningPlan Source, ProvisioningPlan Target);

/// <param name="Fidelity">What the translation loses or approximates, when it does — the same note
/// the generated DDL carries as a warning, shown next to the type rather than only after a plan.</param>
/// <param name="Problem">Why there is no inferred type, when there isn't. Null and
/// <paramref name="TargetType"/> are never both set.</param>
public sealed record InferredColumnType(
    string SourceColumn, string SourceType, string? TargetType, string? Fidelity, string? Problem);

public sealed record ApplyStepResult(string Title, bool Succeeded, string? Error, double ElapsedMs);

public sealed record ApplyResult(IReadOnlyList<ApplyStepResult> Steps, ProvisioningState State);
