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
        [ProvisioningActions.EnableSourceChangeCapture, ProvisioningActions.CreateTargetTable];

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
        _ => Task.FromResult(new ProvisioningPlan(
            request.Action, ProvisioningState.Unknown, [], [$"MsSql does not implement provisioning action '{request.Action}'."])),
    };

    private static async Task<ProvisioningPlan> PlanEnableSourceChangeCaptureAsync(
        DbConnection connection, ProvisioningRequest request, CancellationToken cancellationToken)
    {
        // What needs enabling depends on which reader is configured — a reader kind needing no source
        // cooperation at all (Watermark, BatchReload) is satisfied by construction.
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

    private static async Task<ProvisioningPlan> PlanCreateTargetTableAsync(
        DbConnection connection, ProvisioningRequest request, CancellationToken cancellationToken)
    {
        var table = request.Table;
        await MsSqlDialect.Instance.UseDatabaseAsync(connection, table.Database, cancellationToken);

        if (await TableExistsAsync(connection, table.Schema, table.Table, cancellationToken))
            return new ProvisioningPlan(ProvisioningActions.CreateTargetTable, ProvisioningState.Satisfied, [], []);

        return CreateTargetTablePlanner.Plan(MsSqlDialect.Instance, table, request.Columns);
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
