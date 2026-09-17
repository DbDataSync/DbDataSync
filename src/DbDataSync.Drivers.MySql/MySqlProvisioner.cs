using System.Data.Common;
using DbDataSync.Drivers.Abstractions;
using DbDataSync.Drivers.Generic;
using DbDataSync.Core.Sql;

namespace DbDataSync.Drivers.MySql;

/// <summary>
/// MySQL/MariaDB's <see cref="IProvisioner"/>. <see cref="ProvisioningActions.EnableSourceChangeCapture"/>
/// is <see cref="ProvisioningState.Satisfied"/> with zero steps unconditionally for the generic readers
/// (<c>Watermark</c>, <c>BatchReload</c>) — the MySQL driver registers only the generic pipeline plus
/// <c>TriggerAudit</c>, and only the latter needs source cooperation. The binlog-based native option
/// (<c>change-tracking-mysql.md</c>) fills this in whenever that becomes a phase.
/// <see cref="ProvisioningActions.CreateTargetTable"/> is fully implemented.
/// </summary>
public static class MySqlProvisioner
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
            request.Action, ProvisioningState.Unknown, [], [$"MySQL/MariaDB does not implement provisioning action '{request.Action}'."])),
    };

    /// <summary>
    /// Only the trigger-audit reader needs anything of a MySQL/MariaDB source. Three triggers, not one
    /// (see <see cref="MySqlTriggerAudit"/>), and a version-gated stacking check ahead of creating them:
    /// a server predating MySQL 5.7.2/MariaDB 10.2.1 allows only one trigger per table/timing/event
    /// combination, and DbDataSync's own trigger conflicts with anything an operator already has on the
    /// same events on a server that old.
    /// </summary>
    private static async Task<ProvisioningPlan> PlanEnableSourceChangeCaptureAsync(
        DbConnection connection, ProvisioningRequest request, CancellationToken cancellationToken)
    {
        if (request.ReaderKind != GenericDriverKinds.TriggerAudit)
            return new ProvisioningPlan(ProvisioningActions.EnableSourceChangeCapture, ProvisioningState.Satisfied, [], []);

        var table = request.Table;
        await MySqlDialect.Instance.UseDatabaseAsync(connection, table.Database, cancellationToken);

        var columns = await MySqlCatalog.Instance.GetColumnsAsync(
            connection, table.Schema, table.Table, cancellationToken);
        var keyColumns = columns.Where(c => c.IsPrimaryKey).Select(c => c.Name).ToList();
        var shadowTable = TriggerAuditStatement.ShadowTableName(table.Table);

        var state = new TriggerAuditState(
            await ObjectExistsAsync(connection, table.Schema, shadowTable, cancellationToken),
            await AllTriggersExistAsync(connection, table.Schema, table.Table, cancellationToken),
            keyColumns);

        var steps = new List<ProvisioningStep>();

        if (keyColumns.Count > 0 && !state.ShadowTableExists)
        {
            var definitions = columns
                .Where(c => c.IsPrimaryKey)
                .Select(c => $"{MySqlDialect.Instance.QuoteIdentifier(c.Name)} {c.NativeType} NOT NULL")
                .ToList();

            steps.Add(new ProvisioningStep(
                $"Create the shadow table {MySqlDialect.Instance.QualifyTable(table.Schema, shadowTable)}",
                MySqlTriggerAudit.CreateShadowTable(table.Schema, table.Table, definitions),
                "Holds one row per write to the source table, keyed by the source's primary key. It " +
                "grows until a pass prunes it — see the reader's 'Prune applied changes' setting.",
                ProvisioningStepScope.Table));
        }

        if (keyColumns.Count > 0 && !state.TriggerExists)
        {
            var conflicting = await ConflictingTriggersAsync(connection, table.Schema, table.Table, cancellationToken);
            if (conflicting.Count > 0 && !await SupportsTriggerStackingAsync(connection, cancellationToken))
            {
                return new ProvisioningPlan(
                    ProvisioningActions.EnableSourceChangeCapture,
                    ProvisioningState.Unsupported,
                    [],
                    [$"'{MySqlDialect.Instance.QualifyTable(table.Schema, table.Table)}' already has a trigger " +
                     $"({string.Join(", ", conflicting)}) on an event DbDataSync's own trigger needs, and this " +
                     "server's version does not support more than one trigger per table/timing/event " +
                     "combination (MySQL 5.7.2+/MariaDB 10.2.1+ do). Upgrade the server, or remove or merge " +
                     "the existing trigger, before enabling this."]);
            }

            steps.Add(new ProvisioningStep(
                $"Create the change triggers on {MySqlDialect.Instance.QualifyTable(table.Schema, table.Table)}",
                MySqlTriggerAudit.CreateTriggers(table.Schema, table.Table, keyColumns),
                "MySQL cannot combine INSERT, UPDATE and DELETE in one trigger definition, so this creates " +
                "three — one per event — each with an inline body.",
                ProvisioningStepScope.Table));
        }

        return TriggerAuditPlan.Build(
            MySqlDialect.Instance.QualifyTable(table.Schema, table.Table), state, steps);
    }

    private static async Task<bool> ObjectExistsAsync(
        DbConnection connection, string schema, string name, CancellationToken cancellationToken)
    {
        using var cmd = connection.CreateTimedCommand();
        cmd.CommandText = "SELECT 1 FROM information_schema.tables WHERE table_schema = @schema AND table_name = @name;";
        cmd.AddParameter("@schema", schema);
        cmd.AddParameter("@name", name);
        return await cmd.ExecuteScalarAsync(cancellationToken) is not null;
    }

    private static async Task<bool> AllTriggersExistAsync(
        DbConnection connection, string schema, string table, CancellationToken cancellationToken)
    {
        var existing = await ExistingTriggerNamesAsync(connection, schema, table, cancellationToken);
        return MySqlTriggerAudit.TriggerSuffixes
            .Select(suffix => MySqlTriggerAudit.TriggerName(table, suffix))
            .All(existing.Contains);
    }

    /// <summary>Any trigger already on this table's INSERT/UPDATE/DELETE events that is not one of
    /// DbDataSync's own three — a partial prior run leaving one or two of its own in place is not a
    /// conflict, only a trigger with some other name is.</summary>
    private static async Task<IReadOnlyList<string>> ConflictingTriggersAsync(
        DbConnection connection, string schema, string table, CancellationToken cancellationToken)
    {
        var ownNames = new HashSet<string>(
            MySqlTriggerAudit.TriggerSuffixes.Select(suffix => MySqlTriggerAudit.TriggerName(table, suffix)),
            StringComparer.OrdinalIgnoreCase);

        var existing = await ExistingTriggerNamesAsync(connection, schema, table, cancellationToken);
        return existing.Where(name => !ownNames.Contains(name)).ToList();
    }

    private static async Task<HashSet<string>> ExistingTriggerNamesAsync(
        DbConnection connection, string schema, string table, CancellationToken cancellationToken)
    {
        using var cmd = connection.CreateTimedCommand();
        cmd.CommandText = """
            SELECT trigger_name FROM information_schema.triggers
            WHERE trigger_schema = @schema AND event_object_table = @table
              AND event_manipulation IN ('INSERT', 'UPDATE', 'DELETE');
            """;
        cmd.AddParameter("@schema", schema);
        cmd.AddParameter("@table", table);

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            names.Add(reader.GetString(0));
        return names;
    }

    /// <summary>MySQL 5.7.2+/MariaDB 10.2.1+ allow more than one trigger per table/timing/event
    /// combination; older servers allow exactly one. <c>SELECT VERSION()</c> distinguishes the two
    /// families by the literal substring "MariaDB" every MariaDB build includes in it. An unparseable
    /// version string does not block on a check this cannot evaluate.</summary>
    private static async Task<bool> SupportsTriggerStackingAsync(DbConnection connection, CancellationToken cancellationToken)
    {
        using var cmd = connection.CreateTimedCommand();
        cmd.CommandText = "SELECT VERSION();";
        var raw = await cmd.ExecuteScalarAsync(cancellationToken) as string ?? "";

        var isMariaDb = raw.Contains("MariaDB", StringComparison.OrdinalIgnoreCase);
        var versionPart = raw.Split('-', '+')[0];
        if (!Version.TryParse(versionPart, out var version))
            return true;

        var floor = isMariaDb ? new Version(10, 2, 1) : new Version(5, 7, 2);
        return version >= floor;
    }

    /// <inheritdoc cref="MsSqlProvisioner"/>
    private static async Task<ProvisioningPlan> PlanAlterTargetTableAsync(
        DbConnection connection, ProvisioningRequest request, CancellationToken cancellationToken)
    {
        var table = request.Table;
        await MySqlDialect.Instance.UseDatabaseAsync(connection, table.Database, cancellationToken);

        if (!await TableExistsAsync(connection, table.Schema, table.Table, cancellationToken))
            return new ProvisioningPlan(ProvisioningActions.AlterTargetTable, ProvisioningState.Satisfied, [], []);

        var existing = await MySqlCatalog.Instance.GetColumnsAsync(
            connection, table.Schema, table.Table, cancellationToken);
        return AlterTargetTablePlanner.Plan(MySqlDialect.Instance, table, request.Columns, existing);
    }

    private static async Task<ProvisioningPlan> PlanCreateTargetTableAsync(
        DbConnection connection, ProvisioningRequest request, CancellationToken cancellationToken)
    {
        var table = request.Table;
        await MySqlDialect.Instance.UseDatabaseAsync(connection, table.Database, cancellationToken);

        if (await TableExistsAsync(connection, table.Schema, table.Table, cancellationToken))
            return new ProvisioningPlan(ProvisioningActions.CreateTargetTable, ProvisioningState.Satisfied, [], []);

        return CreateTargetTablePlanner.Plan(MySqlDialect.Instance, table, request.Columns);
    }

    private static async Task<bool> TableExistsAsync(
        DbConnection connection, string schema, string table, CancellationToken cancellationToken)
    {
        using var cmd = connection.CreateTimedCommand();
        cmd.CommandText = """
            SELECT 1 FROM information_schema.tables
            WHERE table_schema = @schema AND table_name = @table AND table_type = 'BASE TABLE';
            """;
        cmd.AddParameter("@schema", schema);
        cmd.AddParameter("@table", table);
        return await cmd.ExecuteScalarAsync(cancellationToken) is not null;
    }
}
