using DataSync.Core.Config;
using DataSync.Drivers.Abstractions;

namespace DataSync.Drivers.Generic;

/// <summary>
/// The <see cref="ProvisioningActions.AlterTargetTable"/> plan: what an **existing** target table is
/// missing, and what no longer matches.
/// <para>
/// **Additive and modifying only, never <c>DROP</c>.** A column the mapping stopped writing is a
/// column something else may still be reading — a report, another replication, somebody's query — and
/// DataSync is not the thing that gets to decide otherwise. The same restraint
/// <see cref="CreateTargetTablePlanner"/> follows, for the same reason.
/// </para>
/// </summary>
public static class AlterTargetTablePlanner
{
    /// <param name="existing">The target's columns as its catalog reports them.</param>
    public static ProvisioningPlan Plan(
        SqlDialect targetDialect,
        TableRef target,
        IReadOnlyList<ProvisioningColumn> wanted,
        IReadOnlyList<ColumnMetadata> existing)
    {
        var unmappable = wanted.Where(c => c.Type.Kind == CanonicalTypeKind.Unmappable).ToList();
        if (unmappable.Count > 0)
        {
            return new ProvisioningPlan(
                ProvisioningActions.AlterTargetTable,
                ProvisioningState.Unsupported,
                [],
                [.. unmappable.Select(c => $"Column '{c.Name}' has no cross-engine type mapping and cannot be altered.")]);
        }

        var byName = existing.ToDictionary(c => c.Name, StringComparer.OrdinalIgnoreCase);
        var qualifiedTable = targetDialect.QualifyTable(target.Schema, target.Table);
        var steps = new List<ProvisioningStep>();
        var warnings = new List<string>();

        foreach (var column in wanted)
        {
            var rendered = targetDialect.RenderColumnType(column.Type);
            if (rendered.Fidelity is not null)
                warnings.Add($"Column '{column.Name}': {rendered.Fidelity}");

            if (!byName.TryGetValue(column.Name, out var actual))
            {
                steps.Add(new ProvisioningStep(
                    $"Add column {column.Name} to {qualifiedTable}",
                    targetDialect.RenderAddColumn(qualifiedTable, column.Name, rendered.Sql),
                    "Added nullable whatever the mapping says: a table with rows cannot gain a NOT NULL " +
                    "column without a default, and inventing one for somebody's data is not this system's call.",
                    ProvisioningStepScope.Table));
                continue;
            }

            // Compared as the target renders them, not as the two catalogs spell them. `int` and
            // `int4` describe the same column on two engines, and a textual comparison of native type
            // names would report a change on every pass and alter a column that was already right.
            if (Matches(targetDialect, actual.NativeType, rendered.Sql))
                continue;

            var alter = targetDialect.RenderAlterColumnType(qualifiedTable, column.Name, rendered.Sql);
            if (alter is null)
            {
                warnings.Add(
                    $"Column '{column.Name}' is '{actual.NativeType}' and should be '{rendered.Sql}', but this " +
                    "engine cannot change it in one statement — change it by hand.");
                continue;
            }

            steps.Add(new ProvisioningStep(
                $"Change {column.Name} from {actual.NativeType} to {rendered.Sql}",
                alter,
                "Widening a type is safe; narrowing one can truncate. Read this before applying it — " +
                "DataSync does not know which of the two this is.",
                ProvisioningStepScope.Table));
        }

        return new ProvisioningPlan(
            ProvisioningActions.AlterTargetTable,
            steps.Count == 0 ? ProvisioningState.Satisfied : ProvisioningState.Missing,
            steps,
            warnings);
    }

    /// <summary>
    /// Whether the target's column already is what the mapping wants.
    /// <para>
    /// Both sides are put through the target's own canonical translation before comparing, so
    /// <c>varchar(50)</c> and <c>VARCHAR (50)</c> are the same answer, and an engine that reports a
    /// type under a synonym does not look like a change. A type the target's own catalog reports but
    /// the dialect cannot translate is treated as **matching**, because the alternative is altering a
    /// column on the strength of not understanding it.
    /// </para>
    /// </summary>
    private static bool Matches(SqlDialect dialect, string actualNativeType, string wantedSql)
    {
        if (string.Equals(actualNativeType, wantedSql, StringComparison.OrdinalIgnoreCase))
            return true;

        try
        {
            var actual = dialect.ToCanonicalType(actualNativeType);
            if (actual.Kind == CanonicalTypeKind.Unmappable)
                return true;

            return string.Equals(dialect.RenderColumnType(actual).Sql, wantedSql, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is NotSupportedException or InvalidOperationException or FormatException)
        {
            return true;
        }
    }
}
