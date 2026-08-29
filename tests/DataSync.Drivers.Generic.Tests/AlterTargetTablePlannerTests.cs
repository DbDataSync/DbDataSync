using DataSync.Core.Config;
using DataSync.Drivers.Abstractions;
using DataSync.Drivers.Generic;

namespace DataSync.Drivers.Generic.Tests;

/// <summary>
/// What an existing target is missing, and what no longer matches. The restraint is the point:
/// additive and modifying only, never DROP — a column the mapping stopped writing is a column
/// something else may still be reading.
/// </summary>
public sealed class AlterTargetTablePlannerTests
{
    private static readonly TableRef Target = new()
    {
        ConnectionName = "tgt", Database = "DW", Schema = "dbo", Table = "Orders",
    };

    private static ProvisioningColumn Wanted(string name, CanonicalTypeKind kind = CanonicalTypeKind.Int32) =>
        new(name, new CanonicalType(kind, kind == CanonicalTypeKind.String ? 50 : null, null, null, true, false),
            IsNullable: true, IsPrimaryKey: false);

    private static ColumnMetadata Existing(string name, string nativeType) =>
        new(name, nativeType, IsNullable: true, IsPrimaryKey: false, IsIdentity: false);

    private static ProvisioningPlan Plan(
        IReadOnlyList<ProvisioningColumn> wanted, IReadOnlyList<ColumnMetadata> existing) =>
        AlterTargetTablePlanner.Plan(RoundTripDialect.Instance, Target, wanted, existing);

    [Fact]
    public void ATargetThatAlreadyMatches_HasNothingToDo()
    {
        var plan = Plan([Wanted("Id")], [Existing("Id", "int")]);

        Assert.Equal(ProvisioningState.Satisfied, plan.State);
        Assert.Empty(plan.Steps);
    }

    [Fact]
    public void AMappedColumnTheTargetLacks_IsAdded()
    {
        var plan = Plan([Wanted("Id"), Wanted("Region", CanonicalTypeKind.String)], [Existing("Id", "int")]);

        Assert.Equal(ProvisioningState.Missing, plan.State);
        var step = Assert.Single(plan.Steps);
        Assert.Contains("Add column Region", step.Title);
        Assert.Contains("ALTER TABLE", step.CommandText);
        Assert.Contains("[Region]", step.CommandText);
    }

    /// <summary>
    /// Nullable whatever the mapping says. A table with rows in it cannot gain a NOT NULL column
    /// without a default, and inventing a default for somebody's data is not this system's call.
    /// </summary>
    [Fact]
    public void AnAddedColumn_IsNullableEvenWhenTheMappingIsNot()
    {
        var wanted = new ProvisioningColumn(
            "Region", new CanonicalType(CanonicalTypeKind.String, 50, null, null, true, false),
            IsNullable: false, IsPrimaryKey: false);

        var step = Assert.Single(Plan([wanted], []).Steps);

        Assert.Contains("NULL", step.CommandText);
        Assert.DoesNotContain("NOT NULL", step.CommandText);
    }

    [Fact]
    public void AColumnWhoseTypeChanged_IsAltered()
    {
        var plan = Plan([Wanted("Id", CanonicalTypeKind.Int64)], [Existing("Id", "int")]);

        var step = Assert.Single(plan.Steps);
        Assert.Contains("Change Id from int", step.Title);
        Assert.Contains("ALTER COLUMN", step.CommandText);
    }

    /// <summary>
    /// Compared as the target renders them, not as the two catalogs spell them. A textual comparison
    /// would report a change on every pass and alter a column that was already right.
    /// </summary>
    [Fact]
    public void ATypeSpeltDifferentlyButMeaningTheSame_IsNotAChange()
    {
        var plan = Plan([Wanted("Id")], [Existing("Id", "INTEGER")]);

        Assert.Equal(ProvisioningState.Satisfied, plan.State);
    }

    /// <summary>
    /// A column the target has and the mapping does not is left alone — something else may still be
    /// reading it, and DataSync is not the thing that decides otherwise.
    /// </summary>
    [Fact]
    public void AColumnTheMappingNoLongerWrites_IsNeverDropped()
    {
        var plan = Plan([Wanted("Id")], [Existing("Id", "int"), Existing("Retired", "varchar(10)")]);

        Assert.Equal(ProvisioningState.Satisfied, plan.State);
        Assert.DoesNotContain(plan.Steps, s => s.CommandText.Contains("DROP", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void AnUnmappableType_IsReportedRatherThanGuessedAt()
    {
        var plan = Plan([Wanted("Weird", CanonicalTypeKind.Unmappable)], [Existing("Id", "int")]);

        Assert.Equal(ProvisioningState.Unsupported, plan.State);
        Assert.Empty(plan.Steps);
        Assert.Contains(plan.Warnings, w => w.Contains("Weird"));
    }

    /// <summary>
    /// An engine that cannot express the change in one statement says so and names the column, rather
    /// than emitting something that might silently truncate.
    /// </summary>
    [Fact]
    public void AnEngineThatCannotAlterInPlace_WarnsInsteadOfEmitting()
    {
        var plan = AlterTargetTablePlanner.Plan(
            NoAlterDialect.Instance, Target, [Wanted("Id", CanonicalTypeKind.Int64)], [Existing("Id", "int")]);

        Assert.Empty(plan.Steps);
        Assert.Contains(plan.Warnings, w => w.Contains("Id") && w.Contains("by hand"));
    }

    /// <summary>
    /// A dialect that can translate its own native types back, which is what the comparison needs: the
    /// planner decides "changed" by rendering both sides through the target's own translation, so a
    /// dialect that cannot read its own catalog answers would report everything as already correct.
    /// </summary>
    private class RoundTripDialect : SqlDialect
    {
        public static RoundTripDialect Instance { get; } = new();

        public override string QuoteIdentifier(string identifier) => $"[{identifier}]";
        public override string ParameterReference(string name) => $"@{name}";

        public override CanonicalType ToCanonicalType(string nativeType) => nativeType.ToLowerInvariant() switch
        {
            "int" or "integer" or "int32" => New(CanonicalTypeKind.Int32),
            "bigint" or "int64" => New(CanonicalTypeKind.Int64),
            var s when s.StartsWith("varchar") || s == "string" => New(CanonicalTypeKind.String, 50),
            _ => New(CanonicalTypeKind.Unmappable),
        };

        public override RenderedColumnType RenderColumnType(CanonicalType type) =>
            new(type.Kind.ToString().ToUpperInvariant(), null);

        private static CanonicalType New(CanonicalTypeKind kind, int? length = null) =>
            new(kind, length, null, null, true, false);
    }

    /// <summary>A dialect that reports it cannot change a column's type in place.</summary>
    private sealed class NoAlterDialect : RoundTripDialect
    {
        public static new NoAlterDialect Instance { get; } = new();

        public override string? RenderAlterColumnType(string qualifiedTable, string column, string type) => null;
    }
}
