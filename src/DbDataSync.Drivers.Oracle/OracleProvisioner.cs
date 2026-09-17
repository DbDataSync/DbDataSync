using System.Data.Common;
using DbDataSync.Drivers.Abstractions;
using DbDataSync.Drivers.Generic;
using DbDataSync.Core.Sql;

namespace DbDataSync.Drivers.Oracle;

/// <summary>
/// Oracle's <see cref="IProvisioner"/>. <see cref="ProvisioningActions.EnableSourceChangeCapture"/> has
/// three cases, not the one every other driver's provisioner branches on — the generic readers
/// (<c>Watermark</c>, <c>BatchReload</c>) need nothing, <c>TriggerAudit</c> needs the DDL
/// <see cref="OracleTriggerAudit"/> generates, and <see cref="OracleDriverKinds.Flashback"/> needs no
/// DDL at all but does need its own grants named at the point of choice, the same posture
/// <c>TriggerAuditPlan.Costs</c> already takes for the trigger option.
/// </summary>
public static class OracleProvisioner
{
    /// <summary>
    /// Stated only because it was tested, not assumed: <c>EXECUTE ON DBMS_FLASHBACK</c> is needed
    /// regardless of table ownership (a plain connection got <c>ORA-00904</c> without it). <c>FLASHBACK</c>
    /// (or <c>FLASHBACK ANY TABLE</c>) only matters when the connecting user does not own the table —
    /// Oracle's ordinary object-privilege model already gives an owner full rights on their own objects,
    /// confirmed by testing this reader against a table the connecting user owned with no per-table
    /// <c>FLASHBACK</c> grant at all.
    /// </summary>
    private static readonly IReadOnlyList<string> FlashbackCosts =
    [
        "This connection's user needs EXECUTE on DBMS_FLASHBACK — not granted by default on any Oracle " +
        "instance tested. Ask a DBA for it before turning this on.",
        "FLASHBACK (or FLASHBACK ANY TABLE) is needed only if this connection reads a table it does not " +
        "own — an owner already has full rights on its own objects.",
        "Bounded by UNDO_RETENTION and actual undo tablespace size. A poll interval that falls badly " +
        "behind for long enough will see a position expire; the recovery is a reload, which already exists.",
    ];

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
            request.Action, ProvisioningState.Unknown, [], [$"Oracle does not implement provisioning action '{request.Action}'."])),
    };

    private static async Task<ProvisioningPlan> PlanEnableSourceChangeCaptureAsync(
        DbConnection connection, ProvisioningRequest request, CancellationToken cancellationToken)
    {
        if (request.ReaderKind == OracleDriverKinds.Flashback)
        {
            return new ProvisioningPlan(
                ProvisioningActions.EnableSourceChangeCapture, ProvisioningState.Satisfied, [], FlashbackCosts);
        }

        if (request.ReaderKind != GenericDriverKinds.TriggerAudit)
            return new ProvisioningPlan(ProvisioningActions.EnableSourceChangeCapture, ProvisioningState.Satisfied, [], []);

        var table = request.Table;
        var columns = await OracleCatalog.Instance.GetColumnsAsync(
            connection, table.Schema, table.Table, cancellationToken);
        var keyColumns = columns.Where(c => c.IsPrimaryKey).Select(c => c.Name).ToList();
        var shadowTable = TriggerAuditStatement.ShadowTableName(table.Table);

        var state = new TriggerAuditState(
            await ObjectExistsAsync(connection, table.Schema, shadowTable, cancellationToken),
            await TriggerExistsAsync(connection, table.Schema, OracleTriggerAudit.TriggerName(table.Table), cancellationToken),
            keyColumns);

        var steps = new List<ProvisioningStep>();

        if (keyColumns.Count > 0 && !state.ShadowTableExists)
        {
            var definitions = columns
                .Where(c => c.IsPrimaryKey)
                .Select(c => $"{OracleDialect.Instance.QuoteIdentifier(c.Name)} {c.NativeType} NOT NULL")
                .ToList();

            steps.Add(new ProvisioningStep(
                $"Create the shadow table {OracleDialect.Instance.QualifyTable(table.Schema, shadowTable)}",
                OracleTriggerAudit.CreateShadowTable(table.Schema, table.Table, definitions),
                "Holds one row per write to the source table, keyed by the source's primary key. It " +
                "grows until a pass prunes it — see the reader's 'Prune applied changes' setting.",
                ProvisioningStepScope.Table));
        }

        if (keyColumns.Count > 0 && !state.TriggerExists)
            steps.Add(new ProvisioningStep(
                $"Create the change trigger on {OracleDialect.Instance.QualifyTable(table.Schema, table.Table)}",
                OracleTriggerAudit.CreateTrigger(table.Schema, table.Table, keyColumns),
                "One trigger, branching on INSERTING/UPDATING/DELETING — Oracle, unlike MySQL, can " +
                "combine all three events in one definition. Needs only ordinary CREATE TRIGGER rights, " +
                "none of the DBA-level grants the Flashback alternative needs.",
                ProvisioningStepScope.Table));

        return TriggerAuditPlan.Build(
            OracleDialect.Instance.QualifyTable(table.Schema, table.Table), state, steps);
    }

    private static async Task<bool> ObjectExistsAsync(
        DbConnection connection, string schema, string name, CancellationToken cancellationToken)
    {
        using var cmd = connection.CreateTimedCommand();
        cmd.CommandText = "SELECT 1 FROM all_tables WHERE owner = :schema AND table_name = :name";
        cmd.AddParameter("schema", schema.ToUpperInvariant());
        cmd.AddParameter("name", name.ToUpperInvariant());
        return await cmd.ExecuteScalarAsync(cancellationToken) is not null;
    }

    private static async Task<bool> TriggerExistsAsync(
        DbConnection connection, string schema, string trigger, CancellationToken cancellationToken)
    {
        using var cmd = connection.CreateTimedCommand();
        cmd.CommandText = "SELECT 1 FROM all_triggers WHERE owner = :schema AND trigger_name = :triggerName";
        cmd.AddParameter("schema", schema.ToUpperInvariant());
        cmd.AddParameter("triggerName", trigger.ToUpperInvariant());
        return await cmd.ExecuteScalarAsync(cancellationToken) is not null;
    }

    /// <inheritdoc cref="MsSqlProvisioner"/>
    private static async Task<ProvisioningPlan> PlanAlterTargetTableAsync(
        DbConnection connection, ProvisioningRequest request, CancellationToken cancellationToken)
    {
        var table = request.Table;

        if (!await TableExistsAsync(connection, table.Schema, table.Table, cancellationToken))
            return new ProvisioningPlan(ProvisioningActions.AlterTargetTable, ProvisioningState.Satisfied, [], []);

        var existing = await OracleCatalog.Instance.GetColumnsAsync(
            connection, table.Schema, table.Table, cancellationToken);
        return AlterTargetTablePlanner.Plan(OracleDialect.Instance, table, request.Columns, existing);
    }

    private static async Task<ProvisioningPlan> PlanCreateTargetTableAsync(
        DbConnection connection, ProvisioningRequest request, CancellationToken cancellationToken)
    {
        var table = request.Table;

        if (await TableExistsAsync(connection, table.Schema, table.Table, cancellationToken))
            return new ProvisioningPlan(ProvisioningActions.CreateTargetTable, ProvisioningState.Satisfied, [], []);

        return CreateTargetTablePlanner.Plan(OracleDialect.Instance, table, request.Columns);
    }

    private static async Task<bool> TableExistsAsync(
        DbConnection connection, string schema, string table, CancellationToken cancellationToken)
    {
        using var cmd = connection.CreateTimedCommand();
        cmd.CommandText = "SELECT 1 FROM all_tables WHERE owner = :schema AND table_name = :tableName";
        cmd.AddParameter("schema", schema.ToUpperInvariant());
        cmd.AddParameter("tableName", table.ToUpperInvariant());
        return await cmd.ExecuteScalarAsync(cancellationToken) is not null;
    }
}
