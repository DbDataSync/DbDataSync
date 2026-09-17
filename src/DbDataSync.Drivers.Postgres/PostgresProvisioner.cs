using System.Data.Common;
using DbDataSync.Drivers.Abstractions;
using DbDataSync.Drivers.Generic;
using DbDataSync.Core.Sql;

namespace DbDataSync.Drivers.Postgres;

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


    /// <summary>
    /// What a logical-decoding source has to have before a slot is worth proposing — phase 34.
    /// <para>
    /// Three checks, in the order an operator can act on them. <c>wal_level = logical</c> is first
    /// because it is the only one that cannot be fixed by a statement: it needs a server restart, and
    /// it is the single biggest adoption obstacle this mechanism has, so it is worth saying before
    /// anything else rather than letting slot creation fail with the server's own wording.
    /// </para>
    /// <para>
    /// The output-plugin allowlist is second, and it is newer than this phase's own plan: a recent
    /// security fix means a plugin that is installed is not thereby permitted.
    /// </para>
    /// <para>
    /// The table's ability to report its own deletes is third, and it is a **refusal** rather than a
    /// warning. A table with no primary key and <c>REPLICA IDENTITY DEFAULT</c> produces no delete
    /// records at all — not an error, not a warning, the deletes simply are not in the WAL — and this
    /// reader declares <c>DetectsDeletes</c>. Discovering that as a target which never loses rows is
    /// the worst possible way to find out.
    /// </para>
    /// <para>
    /// <c>REPLICA IDENTITY FULL</c> is deliberately *not* recommended: <c>DEFAULT</c> already puts the
    /// primary key in the delete and update-old record, which is all this needs, and <c>FULL</c> makes
    /// every update write every column to the WAL. Said out loud because the instinct on reading
    /// "replica identity" for the first time is to turn it up.
    /// </para>
    /// </summary>
    private static async Task<ProvisioningPlan> PlanLogicalSlotAsync(
        DbConnection connection, ProvisioningRequest request, CancellationToken cancellationToken)
    {
        var table = request.Table;
        var qualified = PostgresDialect.Instance.QualifyTable(table.Schema, table.Table);
        await PostgresDialect.Instance.UseDatabaseAsync(connection, table.Database, cancellationToken);

        var warnings = new List<string>();

        var walLevel = await ScalarAsync(connection, PgLogicalSlotStatement.WalLevel, cancellationToken) as string;
        if (!string.Equals(walLevel, "logical", StringComparison.OrdinalIgnoreCase))
        {
            return new ProvisioningPlan(
                ProvisioningActions.EnableSourceChangeCapture, ProvisioningState.Unsupported, [],
                [$"This server's wal_level is '{walLevel}', and logical decoding needs 'logical'. That " +
                 "is a postgresql.conf setting (or an RDS/Aurora parameter group) and it requires a " +
                 "server restart, so it is not something DbDataSync can apply for you. Nothing else " +
                 "about this mapping can be planned until it is changed."]);
        }

        // Second, and newer than this phase's own plan: since PostgreSQL 18.6/17.11/16.15/15.19/14.24
        // an output plugin library has to be on an allowlist before a slot may use it (the fix for
        // CVE-2026-6471). A null means this server predates the setting and restricts nothing.
        var allowed = await ScalarAsync(connection, PgLogicalSlotStatement.OutputPluginLibraries, cancellationToken) as string;
        if (allowed is not null && !allowed.Split(',').Select(p => p.Trim()).Contains(PgLogicalSlotStatement.Plugin))
        {
            return new ProvisioningPlan(
                ProvisioningActions.EnableSourceChangeCapture, ProvisioningState.Unsupported, [],
                [$"This server's output_plugin_libraries is '{allowed}', which does not include " +
                 $"'{PgLogicalSlotStatement.Plugin}'. Since PostgreSQL 18.6, 17.11, 16.15, 15.19 and " +
                 "14.24 an output plugin has to be listed there before a slot may use it — the fix for " +
                 $"CVE-2026-6471 — so a slot created with it would be refused with \"library " +
                 $"\\\"{PgLogicalSlotStatement.Plugin}\\\" may not be used as an output plugin\". Add it: " +
                 $"output_plugin_libraries = '{allowed}, {PgLogicalSlotStatement.Plugin}'. Unlike " +
                 "wal_level this one only needs a reload (SELECT pg_reload_conf()), not a restart."]);
        }

        var identity = await ReadReplicaIdentityAsync(connection, table.Schema, table.Table, cancellationToken);
        if (identity is null)
        {
            return new ProvisioningPlan(
                ProvisioningActions.EnableSourceChangeCapture, ProvisioningState.Unsupported, [],
                [$"Table {qualified} was not found on this server."]);
        }

        // 'd' default, 'n' nothing, 'f' full, 'i' a named index.
        var (relReplIdent, hasPrimaryKey) = identity.Value;
        if (relReplIdent == 'n' || (relReplIdent == 'd' && !hasPrimaryKey))
        {
            var why = relReplIdent == 'n'
                ? "its REPLICA IDENTITY is NOTHING"
                : "it has no primary key and its REPLICA IDENTITY is DEFAULT, which means the primary key";
            return new ProvisioningPlan(
                ProvisioningActions.EnableSourceChangeCapture, ProvisioningState.Unsupported, [],
                [$"{qualified} cannot report its own deletes through logical decoding, because {why}. " +
                 "Postgres writes no delete record at all for such a table — the deletes are not " +
                 "missing from this reader, they are not in the write-ahead log — so a target would " +
                 "quietly keep rows the source no longer has. Give the table a primary key, or set " +
                 $"REPLICA IDENTITY USING INDEX to a unique, non-partial, NOT NULL index. (ALTER TABLE {qualified} " +
                 "REPLICA IDENTITY FULL also works and is not recommended: it makes every update write " +
                 "every column to the WAL, and DEFAULT already carries everything this needs.)"]);
        }

        if (relReplIdent == 'f')
            warnings.Add(
                $"{qualified} has REPLICA IDENTITY FULL. It works, and it costs more than it needs to: " +
                "every update writes every column to the WAL, where DEFAULT would write only the key, " +
                "which is all this reader uses.");

        var slot = LogicalSlotName(request);
        var existing = await ReadSlotPluginAsync(connection, slot, cancellationToken);

        if (existing is not null && !string.Equals(existing, PgLogicalSlotStatement.Plugin, StringComparison.Ordinal))
        {
            return new ProvisioningPlan(
                ProvisioningActions.EnableSourceChangeCapture, ProvisioningState.Unsupported, [],
                [$"A replication slot named '{slot}' already exists on this server and uses the " +
                 $"'{existing}' output plugin, not '{PgLogicalSlotStatement.Plugin}'. Choose another " +
                 "slot name for this mapping. Dropping and recreating that one would discard everything " +
                 "it is holding for whatever is reading it."]);
        }

        var steps = new List<ProvisioningStep>();
        if (existing is null)
        {
            steps.Add(new ProvisioningStep(
                $"Create the logical replication slot '{slot}'",
                PgLogicalSlotStatement.RenderCreateSlot(slot),
                "The slot is what makes the source keep write-ahead log this replication has not read " +
                "yet, and it is the first thing DbDataSync has ever created that outlives a run. It " +
                "holds WAL from the moment it is created until something advances it — so a slot left " +
                "behind by a replication nobody deleted properly will fill the source's disk. Dropping " +
                $"it is `{PgLogicalSlotStatement.RenderDropSlot(slot)}`, and nothing does that " +
                "automatically yet.",
                ProvisioningStepScope.Database));
        }

        return new ProvisioningPlan(
            ProvisioningActions.EnableSourceChangeCapture,
            steps.Count == 0 ? ProvisioningState.Satisfied : ProvisioningState.Missing,
            steps,
            warnings);
    }

    private static string LogicalSlotName(ProvisioningRequest request) =>
        request.ReaderOptions.TryGetValue(PgLogicalSlotReader.SlotNameOption, out var configured)
        && !string.IsNullOrWhiteSpace(configured)
            ? configured.Trim()
            : PgLogicalSlotStatement.DefaultSlotName(request.Table.Schema, request.Table.Table);

    private static async Task<(char RelReplIdent, bool HasPrimaryKey)?> ReadReplicaIdentityAsync(
        DbConnection connection, string schema, string table, CancellationToken cancellationToken)
    {
        using var cmd = connection.CreateTimedCommand();
        cmd.CommandText = PgLogicalSlotStatement.ReplicaIdentity;
        cmd.AddParameter("@schema", schema);
        cmd.AddParameter("@table", table);

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            return null;
        return (reader.GetChar(0), reader.GetBoolean(1));
    }

    private static async Task<string?> ReadSlotPluginAsync(
        DbConnection connection, string slot, CancellationToken cancellationToken)
    {
        using var cmd = connection.CreateTimedCommand();
        cmd.CommandText = PgLogicalSlotStatement.SlotState;
        cmd.AddParameter("@slot", slot);
        cmd.AddParameter("@stored", DBNull.Value);

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? reader.GetString(0) : null;
    }

    private static async Task<object?> ScalarAsync(
        DbConnection connection, string sql, CancellationToken cancellationToken)
    {
        using var cmd = connection.CreateTimedCommand();
        cmd.CommandText = sql;
        return await cmd.ExecuteScalarAsync(cancellationToken);
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

    private static async Task<bool> TriggerExistsAsync(
        DbConnection connection, string schema, string table, string trigger, CancellationToken cancellationToken)
    {
        using var cmd = connection.CreateTimedCommand();
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
