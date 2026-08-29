using System.Data.Common;
using DataSync.Drivers.Abstractions;
using DataSync.Drivers.Generic;

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
        ProvisioningActions.EnableSourceChangeCapture => Task.FromResult(
            new ProvisioningPlan(ProvisioningActions.EnableSourceChangeCapture, ProvisioningState.Satisfied, [], [])),
        ProvisioningActions.CreateTargetTable => PlanCreateTargetTableAsync(connection, request, cancellationToken),
        ProvisioningActions.AlterTargetTable => PlanAlterTargetTableAsync(connection, request, cancellationToken),
        _ => Task.FromResult(new ProvisioningPlan(
            request.Action, ProvisioningState.Unknown, [], [$"Postgres does not implement provisioning action '{request.Action}'."])),
    };

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
