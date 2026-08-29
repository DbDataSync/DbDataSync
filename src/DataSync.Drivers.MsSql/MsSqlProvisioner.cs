using System.Data.Common;
using DataSync.Drivers.Abstractions;
using DataSync.Drivers.Generic;

namespace DataSync.Drivers.MsSql;

/// <summary>
/// SQL Server's <see cref="IProvisioner"/> — see phase 25 §3. Its state-detection queries and decision
/// logic (<see cref="BuildEnableChangeCaptureSteps"/>) are deliberately split from the async catalog
/// lookups that feed them, so the decision logic — no primary key → Unsupported; database on, table off
/// → exactly one step; etc. — is unit-testable against plain booleans instead of a live server.
/// </summary>
public static class MsSqlProvisioner
{
    public static IReadOnlyList<string> SupportedActions { get; } =
        [ProvisioningActions.EnableSourceChangeCapture, ProvisioningActions.CreateTargetTable,
         ProvisioningActions.AlterTargetTable];

    /// <summary>
    /// A placeholder, not a considered answer — see phase 25's open question on retention. The harness
    /// uses <c>2 DAYS, AUTO_CLEANUP = OFF</c>, which is a harness choice, not a recommendation; deriving
    /// this from <c>SchedulingConfig</c> is real follow-on work.
    /// </summary>
    public const int DefaultChangeRetentionDays = 7;

    public static Task<ProvisioningPlan> PlanAsync(
        DbConnection connection, ProvisioningRequest request, CancellationToken cancellationToken) => request.Action switch
    {
        ProvisioningActions.EnableSourceChangeCapture => PlanEnableSourceChangeCaptureAsync(connection, request, cancellationToken),
        ProvisioningActions.CreateTargetTable => PlanCreateTargetTableAsync(connection, request, cancellationToken),
        ProvisioningActions.AlterTargetTable => PlanAlterTargetTableAsync(connection, request, cancellationToken),
        _ => Task.FromResult(new ProvisioningPlan(
            request.Action, ProvisioningState.Unknown, [], [$"MsSql does not implement provisioning action '{request.Action}'."])),
    };

    private static async Task<ProvisioningPlan> PlanEnableSourceChangeCaptureAsync(
        DbConnection connection, ProvisioningRequest request, CancellationToken cancellationToken)
    {
        // What needs enabling depends on which reader is configured — a reader kind needing no source
        // cooperation at all (Watermark, BatchReload) is satisfied by construction.
        if (request.ReaderKind == MsSqlDriverKinds.Cdc)
            return await PlanEnableCdcAsync(connection, request, cancellationToken);

        if (request.ReaderKind != MsSqlDriverKinds.ChangeTracking)
            return new ProvisioningPlan(ProvisioningActions.EnableSourceChangeCapture, ProvisioningState.Satisfied, [], []);

        var table = request.Table;
        await MsSqlDialect.Instance.UseDatabaseAsync(connection, table.Database, cancellationToken);
        var qualifiedTable = MsSqlDialect.Instance.QualifyTable(table.Schema, table.Table);

        var hasPrimaryKey = (await MsSqlSchemaQueries.GetPrimaryKeyColumnsAsync(
            connection, table.Schema, table.Table, cancellationToken)).Count > 0;
        var databaseLevel = await IsChangeTrackingEnabledAtDatabaseAsync(connection, table.Database, cancellationToken);
        var tableLevel = await IsChangeTrackingEnabledAtTableAsync(connection, qualifiedTable, cancellationToken);

        var snapshotRequested = IsSnapshotIsolationRequested(request.ReaderOptions);
        var snapshotEnabled = snapshotRequested
            ? await IsSnapshotIsolationEnabledAsync(connection, table.Database, cancellationToken)
            : (bool?)null;

        return BuildEnableChangeCaptureSteps(
            table.Database, qualifiedTable, hasPrimaryKey, databaseLevel, tableLevel,
            snapshotRequested, snapshotEnabled, DefaultChangeRetentionDays);
    }

    /// <summary>The decision logic in isolation — see the type doc. Every branch here is a reason the
    /// plan can stop or a step it can emit; none of it touches a connection.</summary>
    internal static ProvisioningPlan BuildEnableChangeCaptureSteps(
        string database,
        string qualifiedTable,
        bool hasPrimaryKey,
        bool changeTrackingEnabledAtDatabase,
        bool changeTrackingEnabledAtTable,
        bool snapshotIsolationRequested,
        bool? snapshotIsolationEnabled,
        int retentionDays)
    {
        if (!hasPrimaryKey)
            return new ProvisioningPlan(
                ProvisioningActions.EnableSourceChangeCapture,
                ProvisioningState.Unsupported,
                [],
                [$"'{qualifiedTable}' has no primary key. Change Tracking requires one."]);

        var steps = new List<ProvisioningStep>();

        if (!changeTrackingEnabledAtDatabase)
            steps.Add(new ProvisioningStep(
                $"Enable Change Tracking on database [{database}]",
                $"ALTER DATABASE [{database}] SET CHANGE_TRACKING = ON (CHANGE_RETENTION = {retentionDays} DAYS, AUTO_CLEANUP = ON);",
                "Database-scoped — shared by every mapping on this database.",
                ProvisioningStepScope.Database));

        if (snapshotIsolationRequested && snapshotIsolationEnabled == false)
            steps.Add(new ProvisioningStep(
                $"Allow snapshot isolation on database [{database}]",
                $"ALTER DATABASE [{database}] SET ALLOW_SNAPSHOT_ISOLATION ON;",
                "Needed by this mapping's 'snapshotIsolation' reader option, so a concurrent delete " +
                "cannot strand a reported insert or update.",
                ProvisioningStepScope.Database));

        if (!changeTrackingEnabledAtTable)
            steps.Add(new ProvisioningStep(
                $"Enable Change Tracking on {qualifiedTable}",
                $"ALTER TABLE {qualifiedTable} ENABLE CHANGE_TRACKING WITH (TRACK_COLUMNS_UPDATED = OFF);",
                "TRACK_COLUMNS_UPDATED is off because nothing reads Change Tracking's column mask.",
                ProvisioningStepScope.Table));

        return new ProvisioningPlan(
            ProvisioningActions.EnableSourceChangeCapture,
            steps.Count == 0 ? ProvisioningState.Satisfied : ProvisioningState.Missing,
            steps,
            []);
    }

    private static async Task<ProvisioningPlan> PlanEnableCdcAsync(
        DbConnection connection, ProvisioningRequest request, CancellationToken cancellationToken)
    {
        var table = request.Table;
        await MsSqlDialect.Instance.UseDatabaseAsync(connection, table.Database, cancellationToken);

        var hasPrimaryKey = (await MsSqlSchemaQueries.GetPrimaryKeyColumnsAsync(
            connection, table.Schema, table.Table, cancellationToken)).Count > 0;
        var databaseLevel = await MsSqlCdcCatalog.CdcIsEnabledAsync(connection, cancellationToken);
        var instance = databaseLevel
            ? await MsSqlCdcCatalog.FindCaptureInstanceAsync(connection, table.Schema, table.Table, cancellationToken)
            : null;

        return BuildEnableCdcSteps(table.Database, table.Schema, table.Table, hasPrimaryKey, databaseLevel, instance);
    }

    /// <summary>
    /// The decision logic in isolation, like <see cref="BuildEnableChangeCaptureSteps"/> — every branch
    /// is a reason the plan can stop or a step it can emit, and none of it touches a connection.
    /// </summary>
    internal static ProvisioningPlan BuildEnableCdcSteps(
        string database, string schema, string table, bool hasPrimaryKey, bool cdcEnabledAtDatabase,
        CdcCaptureInstance? existingInstance)
    {
        var steps = new List<ProvisioningStep>();
        var warnings = new List<string>();

        if (!cdcEnabledAtDatabase)
            steps.Add(new ProvisioningStep(
                $"Enable Change Data Capture on database [{database}]",
                $"USE [{database}];\nEXEC sys.sp_cdc_enable_db;",
                "Database-scoped, and it creates the cdc schema, its tables, and two SQL Server Agent " +
                "jobs. CDC does not work without Agent running — which is the thing to check first " +
                "when a capture instance exists and no changes arrive.",
                ProvisioningStepScope.Database));

        if (existingInstance is null)
        {
            // @supports_net_changes needs a primary key. Without one CDC still works, but only in
            // all-changes form — the reader falls back and says so, rather than refusing, because
            // all-changes is a working configuration and not a broken one.
            var netChanges = hasPrimaryKey ? 1 : 0;
            if (!hasPrimaryKey)
                warnings.Add(
                    $"'{schema}.{table}' has no primary key, so this capture instance cannot support " +
                    "net changes. The reader will fall back to reading every intermediate change, " +
                    "which is correct and more work per pass.");

            steps.Add(new ProvisioningStep(
                $"Enable Change Data Capture on {MsSqlDialect.Instance.QualifyTable(schema, table)}",
                $"USE [{database}];\n" +
                $"EXEC sys.sp_cdc_enable_table @source_schema = N'{Literal(schema)}', " +
                $"@source_name = N'{Literal(table)}', @role_name = NULL, " +
                $"@supports_net_changes = {netChanges};",
                "@role_name = NULL means no gating role, so reading is governed by ordinary table " +
                "permissions. @supports_net_changes creates the function that collapses a key's " +
                "changes to one row, which is what makes CDC usable for mirroring.",
                ProvisioningStepScope.Table));
        }
        else if (!existingInstance.SupportsNetChanges && hasPrimaryKey)
        {
            // Not a step: changing it means dropping and recreating the capture instance, which
            // discards captured history. That is a decision, not a repair.
            warnings.Add(
                $"Capture instance '{existingInstance.CaptureInstance}' was created without " +
                "@supports_net_changes, so the reader reads every intermediate change. Changing that " +
                "means disabling and re-enabling capture for this table, which discards the history " +
                "captured so far — so it is left alone here.");
        }

        return new ProvisioningPlan(
            ProvisioningActions.EnableSourceChangeCapture,
            steps.Count == 0 ? ProvisioningState.Satisfied : ProvisioningState.Missing,
            steps,
            warnings);
    }

    /// <summary>A schema or table name inside a string literal, which is what sp_cdc_enable_table takes
    /// — it names its arguments as sysname values, not as identifiers to be quoted.</summary>
    private static string Literal(string value) => value.Replace("'", "''");

    private static async Task<ProvisioningPlan> PlanCreateTargetTableAsync(
        DbConnection connection, ProvisioningRequest request, CancellationToken cancellationToken)
    {
        var table = request.Table;
        await MsSqlDialect.Instance.UseDatabaseAsync(connection, table.Database, cancellationToken);

        if (await TableExistsAsync(connection, table.Schema, table.Table, cancellationToken))
            return new ProvisioningPlan(ProvisioningActions.CreateTargetTable, ProvisioningState.Satisfied, [], []);

        return CreateTargetTablePlanner.Plan(MsSqlDialect.Instance, table, request.Columns);
    }

    /// <summary>
    /// What an existing target is missing or has wrong. A table that is not there at all is not this
    /// action's business — that is <see cref="ProvisioningActions.CreateTargetTable"/>, and the two
    /// are mutually exclusive by construction.
    /// </summary>
    private static async Task<ProvisioningPlan> PlanAlterTargetTableAsync(
        DbConnection connection, ProvisioningRequest request, CancellationToken cancellationToken)
    {
        var table = request.Table;
        await MsSqlDialect.Instance.UseDatabaseAsync(connection, table.Database, cancellationToken);

        if (!await TableExistsAsync(connection, table.Schema, table.Table, cancellationToken))
            return new ProvisioningPlan(ProvisioningActions.AlterTargetTable, ProvisioningState.Satisfied, [], []);

        var existing = await MsSqlSchemaQueries.GetColumnsAsync(connection, table.Schema, table.Table, cancellationToken);
        return AlterTargetTablePlanner.Plan(MsSqlDialect.Instance, table, request.Columns, existing);
    }

    private static bool IsSnapshotIsolationRequested(IReadOnlyDictionary<string, string> readerOptions) =>
        readerOptions.TryGetValue(MsSqlChangeTrackingReader.SnapshotIsolationOption, out var raw)
        && (string.Equals(raw, "true", StringComparison.OrdinalIgnoreCase) || raw == "1");

    private static async Task<bool> TableExistsAsync(
        DbConnection connection, string schema, string table, CancellationToken cancellationToken)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT 1 FROM sys.tables t JOIN sys.schemas s ON t.schema_id = s.schema_id
            WHERE s.name = @schema AND t.name = @table;
            """;
        cmd.AddParameter("@schema", schema);
        cmd.AddParameter("@table", table);
        return await cmd.ExecuteScalarAsync(cancellationToken) is not null;
    }

    private static async Task<bool> IsChangeTrackingEnabledAtDatabaseAsync(
        DbConnection connection, string database, CancellationToken cancellationToken)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM sys.change_tracking_databases WHERE database_id = DB_ID(@database);";
        cmd.AddParameter("@database", database);
        return await cmd.ExecuteScalarAsync(cancellationToken) is not null;
    }

    private static async Task<bool> IsChangeTrackingEnabledAtTableAsync(
        DbConnection connection, string qualifiedTable, CancellationToken cancellationToken)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM sys.change_tracking_tables WHERE object_id = OBJECT_ID(@qualifiedTable);";
        cmd.AddParameter("@qualifiedTable", qualifiedTable);
        return await cmd.ExecuteScalarAsync(cancellationToken) is not null;
    }

    private static async Task<bool> IsSnapshotIsolationEnabledAsync(
        DbConnection connection, string database, CancellationToken cancellationToken)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT snapshot_isolation_state FROM sys.databases WHERE database_id = DB_ID(@database);";
        cmd.AddParameter("@database", database);
        var result = await cmd.ExecuteScalarAsync(cancellationToken);
        return result is not null and not DBNull && Convert.ToInt32(result) == 1;
    }
}
