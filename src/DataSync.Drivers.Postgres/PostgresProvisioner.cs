using System.Data.Common;
using DataSync.Drivers.Abstractions;
using DataSync.Drivers.Generic;
using DataSync.Core.Sql;

namespace DataSync.Drivers.Postgres;

/// <summary>
/// PostgreSQL's <see cref="IProvisioner"/>. <see cref="ProvisioningActions.EnableSourceChangeCapture"/>
/// is <see cref="ProvisioningState.Satisfied"/> with zero steps unconditionally — the Postgres driver
/// registers only the generic pipeline (<c>Watermark</c>, <c>BatchReload</c>), and neither reader needs
/// any source cooperation. Logical replication publications fill this in whenever
/// <c>change-tracking-postgres.md</c> becomes a phase. <see cref="ProvisioningActions.CreateTargetTable"/>
/// is fully implemented.
/// </summary>
public static class PostgresProvisioner
{
    public static IReadOnlyList<string> SupportedActions { get; } =
        [ProvisioningActions.EnableSourceChangeCapture, ProvisioningActions.CreateTargetTable,
         ProvisioningActions.AlterTargetTable];

    public static Task<ProvisioningPlan> PlanAsync(
        DbConnection connection, ProvisioningRequest request, CancellationToken cancellationToken) => request.Action switch
    {
        ProvisioningActions.EnableSourceChangeCapture =>
            PlanEnableSourceChangeCaptureAsync(connection, request, cancellationToken),
        ProvisioningActions.CreateTargetTable => PlanCreateTargetTableAsync(connection, request, cancellationToken),
        ProvisioningActions.AlterTargetTable => PlanAlterTargetTableAsync(connection, request, cancellationToken),
        _ => Task.FromResult(new ProvisioningPlan(
            request.Action, ProvisioningState.Unknown, [], [$"Postgres does not implement provisioning action '{request.Action}'."])),
    };

    /// <summary>
    /// Only the trigger-audit reader needs anything of a Postgres source. The generic scanning readers
    /// (<c>Watermark</c>, <c>BatchReload</c>) are satisfied by construction; logical replication
    /// publications fill the rest in whenever <c>change-tracking-postgres.md</c> becomes a phase.
    /// </summary>
    private static async Task<ProvisioningPlan> PlanEnableSourceChangeCaptureAsync(
        DbConnection connection, ProvisioningRequest request, CancellationToken cancellationToken)
    {
        if (request.ReaderKind != GenericDriverKinds.TriggerAudit)
            return new ProvisioningPlan(ProvisioningActions.EnableSourceChangeCapture, ProvisioningState.Satisfied, [], []);

        var table = request.Table;
        await PostgresDialect.Instance.UseDatabaseAsync(connection, table.Database, cancellationToken);

        var columns = await PostgresCatalog.Instance.GetColumnsAsync(
            connection, table.Schema, table.Table, cancellationToken);
        var keyColumns = columns.Where(c => c.IsPrimaryKey).Select(c => c.Name).ToList();
        var shadowTable = TriggerAuditStatement.ShadowTableName(table.Table);

        var state = new TriggerAuditState(
            await ObjectExistsAsync(connection, table.Schema, shadowTable, cancellationToken),
            await TriggerExistsAsync(
                connection, table.Schema, table.Table, PostgresTriggerAudit.TriggerName(table.Table), cancellationToken),
            keyColumns);

        var steps = new List<ProvisioningStep>();

        if (keyColumns.Count > 0 && !state.ShadowTableExists)
        {
            var definitions = columns
                .Where(c => c.IsPrimaryKey)
                .Select(c => $"{PostgresDialect.Instance.QuoteIdentifier(c.Name)} {c.NativeType} NOT NULL")
                .ToList();

            steps.Add(new ProvisioningStep(
                $"Create the shadow table {PostgresDialect.Instance.QualifyTable(table.Schema, shadowTable)}",
                PostgresTriggerAudit.CreateShadowTable(table.Schema, table.Table, definitions),
                "Holds one row per write to the source table, keyed by the source's primary key. It " +
                "grows until a pass prunes it — see the reader's 'Prune applied changes' setting.",
                ProvisioningStepScope.Table));
        }

        if (keyColumns.Count > 0 && !state.TriggerExists)
            steps.Add(new ProvisioningStep(
                $"Create the change trigger on {PostgresDialect.Instance.QualifyTable(table.Schema, table.Table)}",
                PostgresTriggerAudit.CreateFunctionAndTrigger(table.Schema, table.Table, keyColumns),
                "Postgres has no inline trigger body, so this is a plpgsql function and a trigger that " +
                "calls it. It returns NULL, which is what an AFTER trigger must do to leave the row the " +
                "application wrote alone.",
                ProvisioningStepScope.Table));

        return TriggerAuditPlan.Build(
            PostgresDialect.Instance.QualifyTable(table.Schema, table.Table), state, steps);
    }

    private static async Task<bool> ObjectExistsAsync(
        DbConnection connection, string schema, string name, CancellationToken cancellationToken)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM information_schema.tables WHERE table_schema = @schema AND table_name = @name;";
        cmd.AddParameter("@schema", schema);
        cmd.AddParameter("@name", name);
        return await cmd.ExecuteScalarAsync(cancellationToken) is not null;
    }

    private static async Task<bool> TriggerExistsAsync(
        DbConnection connection, string schema, string table, string trigger, CancellationToken cancellationToken)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT 1 FROM information_schema.triggers
            WHERE trigger_schema = @schema AND event_object_table = @table AND trigger_name = @trigger;
            """;
        cmd.AddParameter("@schema", schema);
        cmd.AddParameter("@table", table);
        cmd.AddParameter("@trigger", trigger);
        return await cmd.ExecuteScalarAsync(cancellationToken) is not null;
    }

    /// <inheritdoc cref="MsSqlProvisioner"/>
    private static async Task<ProvisioningPlan> PlanAlterTargetTableAsync(
        DbConnection connection, ProvisioningRequest request, CancellationToken cancellationToken)
    {
        var table = request.Table;
        await PostgresDialect.Instance.UseDatabaseAsync(connection, table.Database, cancellationToken);

        if (!await TableExistsAsync(connection, table.Schema, table.Table, cancellationToken))
            return new ProvisioningPlan(ProvisioningActions.AlterTargetTable, ProvisioningState.Satisfied, [], []);

        var existing = await PostgresCatalog.Instance.GetColumnsAsync(
            connection, table.Schema, table.Table, cancellationToken);
        return AlterTargetTablePlanner.Plan(PostgresDialect.Instance, table, request.Columns, existing);
    }

    private static async Task<ProvisioningPlan> PlanCreateTargetTableAsync(
        DbConnection connection, ProvisioningRequest request, CancellationToken cancellationToken)
    {
        var table = request.Table;
        await PostgresDialect.Instance.UseDatabaseAsync(connection, table.Database, cancellationToken);

        if (await TableExistsAsync(connection, table.Schema, table.Table, cancellationToken))
            return new ProvisioningPlan(ProvisioningActions.CreateTargetTable, ProvisioningState.Satisfied, [], []);

        return CreateTargetTablePlanner.Plan(PostgresDialect.Instance, table, request.Columns);
    }

    private static async Task<bool> TableExistsAsync(
        DbConnection connection, string schema, string table, CancellationToken cancellationToken)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT 1 FROM information_schema.tables
            WHERE table_schema = @schema AND table_name = @table AND table_type = 'BASE TABLE';
            """;
        cmd.AddParameter("@schema", schema);
        cmd.AddParameter("@table", table);
        return await cmd.ExecuteScalarAsync(cancellationToken) is not null;
    }
}
