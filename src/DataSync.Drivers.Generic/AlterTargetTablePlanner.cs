using DataSync.Core.Config;
using DataSync.Drivers.Abstractions;
using DataSync.Core.Sql;

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
        var unmappable = wanted
            .Where(c => c.TypeOverride is null && c.Type.Kind == CanonicalTypeKind.Unmappable)
            .ToList();
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

        var renames = PendingRenames(wanted, byName);
        if (Collisions(renames, byName) is { Count: > 0 } collisions)
        {
            return new ProvisioningPlan(
                ProvisioningActions.AlterTargetTable,
                ProvisioningState.Unsupported,
                [],
                [.. collisions]);
        }

        foreach (var (column, from) in renames)
        {
            steps.Add(new ProvisioningStep(
                $"Rename {from} to {column.Name} on {qualifiedTable}",
                targetDialect.RenderRenameColumn(qualifiedTable, from, column.Name),
                "Renamed rather than added, so the column's existing rows keep their values. Anything " +
                "outside DataSync that reads this table by the old name stops working the moment this runs.",
                ProvisioningStepScope.Table));

            // Renamed, so the type comparison below is against the column this used to be — not against
            // a column that does not exist yet, which would plan a redundant ADD for one already there.
            byName[column.Name] = byName[from] with { Name = column.Name };
        }

        foreach (var column in wanted)
        {
            var rendered = column.TypeOverride is { } chosen
                ? new RenderedColumnType(chosen, null)
                : targetDialect.RenderColumnType(column.Type);
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
    /// The renames that still have to happen on the target, as (column, name it currently has).
    /// <para>
    /// A column's whole unapplied chain collapses to one rename: the intermediate names were never
    /// written to the target, so renaming <c>A</c> to <c>B</c> to <c>C</c> against a target still
    /// holding <c>A</c> is one statement, not two. A step whose old name is absent from the target is
    /// not a rename at all — the column was renamed by hand, or never existed — and falls through to
    /// the ordinary add-or-alter path.
    /// </para>
    /// </summary>
    private static List<(ProvisioningColumn Column, string From)> PendingRenames(
        IReadOnlyList<ProvisioningColumn> wanted, Dictionary<string, ColumnMetadata> byName)
    {
        var pending = new List<(ProvisioningColumn, string)>();

        foreach (var column in wanted)
        {
            var unapplied = (column.Renames ?? []).Where(r => !r.Applied).ToList();
            if (unapplied.Count == 0)
                continue;

            var from = unapplied[0].From;
            if (!string.Equals(from, column.Name, StringComparison.OrdinalIgnoreCase) && byName.ContainsKey(from))
                pending.Add((column, from));
        }

        return pending;
    }

    /// <summary>
    /// Renames that cannot be run in any order without one clobbering another: a **swap** (<c>A</c> to
    /// <c>B</c> while <c>B</c> goes to <c>A</c>), or a rename onto a name the table already uses.
    /// <para>
    /// Reported as <see cref="ProvisioningState.Unsupported"/> naming the columns, rather than solved.
    /// Untangling one needs a temporary name and an order chosen so no intermediate state loses a
    /// column — that is a real algorithm, it has to be right the first time because getting it wrong
    /// drops somebody's data, and it is <b>not</b> something to invent unreviewed inside a planner.
    /// Renaming the columns in two passes, applying between them, does the same job today.
    /// </para>
    /// </summary>
    private static List<string> Collisions(
        List<(ProvisioningColumn Column, string From)> renames, Dictionary<string, ColumnMetadata> byName)
    {
        var problems = new List<string>();

        foreach (var (column, from) in renames)
        {
            if (!byName.ContainsKey(column.Name))
                continue;

            var occupant = renames.FirstOrDefault(r =>
                string.Equals(r.From, column.Name, StringComparison.OrdinalIgnoreCase));

            if (occupant.Column is null)
            {
                problems.Add(
                    $"Column '{from}' is being renamed to '{column.Name}', which the target already uses. " +
                    "Rename or remove the existing column first — DataSync will not overwrite it.");
                continue;
            }

            // Both halves of a swap collide, and reporting it twice from each side's point of view
            // reads as two problems when it is one.
            var pair = string.CompareOrdinal(from, column.Name) < 0
                ? $"'{from}' and '{column.Name}'"
                : $"'{column.Name}' and '{from}'";
            var message =
                $"Columns {pair} are being renamed past each other. Apply one rename, then the other: " +
                "doing both at once needs a temporary name, and DataSync does not pick one on your behalf.";
            if (!problems.Contains(message))
                problems.Add(message);
        }

        return problems;
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
