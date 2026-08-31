using DataSync.Core.Config;
using DataSync.Drivers.Abstractions;
using DataSync.Core.Sql;

namespace DataSync.Drivers.Generic;

/// <summary>
/// The <see cref="ProvisioningActions.CreateTargetTable"/> plan shared by every driver's provisioner —
/// once state detection (does the table already exist?) says the table is actually missing, rendering
/// its <c>CREATE TABLE</c> is the same recipe on every target engine: check every column translates,
/// render each one through the target dialect, collect fidelity warnings, build the statement.
/// </summary>
public static class CreateTargetTablePlanner
{
    public static ProvisioningPlan Plan(SqlDialect targetDialect, TableRef target, IReadOnlyList<ProvisioningColumn> columns)
    {
        // A column whose type the operator chose needs no cross-engine translation — they have already
        // said what it should be, and refusing on the grounds that we could not have guessed it would
        // be refusing to honour the answer.
        var unmappable = columns
            .Where(c => c.TypeOverride is null && c.Type.Kind == CanonicalTypeKind.Unmappable)
            .ToList();
        if (unmappable.Count > 0)
            return new ProvisioningPlan(
                ProvisioningActions.CreateTargetTable,
                ProvisioningState.Unsupported,
                [],
                unmappable.Select(c => $"Column '{c.Name}' has no cross-engine type mapping and cannot be created.").ToList());

        var warnings = new List<string>();
        var rendered = new List<CreateTableColumn>();
        foreach (var column in columns)
        {
            var renderedType = column.TypeOverride is { } chosen
                ? new RenderedColumnType(chosen, null)
                : targetDialect.RenderColumnType(column.Type);
            if (renderedType.Fidelity is not null)
                warnings.Add($"Column '{column.Name}': {renderedType.Fidelity}");
            rendered.Add(new CreateTableColumn(column.Name, renderedType, column.IsNullable, column.IsPrimaryKey));
        }

        var qualifiedTable = targetDialect.QualifyTable(target.Schema, target.Table);
        var sql = CreateTableStatement.Build(targetDialect, qualifiedTable, rendered);

        var step = new ProvisioningStep(
            $"Create table {qualifiedTable}",
            sql,
            "Additive only — never runs when the table already exists, and never alters one that does.",
            ProvisioningStepScope.Table);

        return new ProvisioningPlan(ProvisioningActions.CreateTargetTable, ProvisioningState.Missing, [step], warnings);
    }
}
