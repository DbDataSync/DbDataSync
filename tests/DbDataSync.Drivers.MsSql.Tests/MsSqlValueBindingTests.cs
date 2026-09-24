using System.Data;
using System.Data.Common;
using DbDataSync.Core.Config;
using DbDataSync.Core.Sql;
using DbDataSync.Drivers.Abstractions;
using DbDataSync.Drivers.Generic;
using Microsoft.Data.SqlClient;
using Xunit;

namespace DbDataSync.Drivers.MsSql.Tests;

/// <summary>
/// SQL Server's own typed value binding (<see cref="MsSqlValueBinding"/>) through the engine-neutral
/// <see cref="SegmentScope"/>, without a live server — this is the layer where a segment stops being a
/// description and becomes SQL, so it's worth pinning exactly. Phase 191S: called directly rather than
/// through the retired <c>MsSqlSegmentScope</c>, a one-line wrapper that added nothing
/// <see cref="SegmentScope.Build"/>'s own dialect/binder parameters didn't already cover.
/// </summary>
public sealed class MsSqlValueBindingTests
{
    /// <summary>The scope holds provider-neutral <see cref="DbParameter"/>s now; the typed binding
    /// these tests exist to pin is still SQL Server's, so they reach through to it explicitly.</summary>
    private static SqlDbType TypeOf(DbParameter parameter) => ((SqlParameter)parameter).SqlDbType;

    private static readonly List<ColumnMetadata> Columns =
    [
        new("OrderId", "int", IsNullable: false, IsPrimaryKey: true, IsIdentity: false),
        new("Region", "nvarchar(20)", IsNullable: true, IsPrimaryKey: false, IsIdentity: false),
        new("Amount", "decimal(18,2)", IsNullable: true, IsPrimaryKey: false, IsIdentity: false),
        new("OrderDate", "datetime2(7)", IsNullable: true, IsPrimaryKey: false, IsIdentity: false),
    ];

    [Fact]
    public void Full_MatchesEverythingAndBindsNothing()
    {
        var scope = SegmentScope.Build(MsSqlDialect.Instance, MsSqlValueBinding.Instance, new FullSegment(), Columns);

        Assert.Equal("1 = 1", scope.Predicate);
        Assert.Empty(scope.Parameters);
    }

    [Fact]
    public void NoSegment_RendersTheSameAsFull()
    {
        Assert.Equal("1 = 1", SegmentScope.Build(MsSqlDialect.Instance, MsSqlValueBinding.Instance, null, Columns).Predicate);
    }

    [Fact]
    public void List_RendersAnInClauseOfTypedParameters()
    {
        var scope = SegmentScope.Build(MsSqlDialect.Instance, MsSqlValueBinding.Instance, new ListSegment("Region", ["EU", "US", "APAC"]), Columns);

        Assert.Equal("[Region] IN (@seg0, @seg1, @seg2)", scope.Predicate);
        Assert.Equal(["EU", "US", "APAC"], scope.Parameters.Select(p => p.Value));
        Assert.All(scope.Parameters, p => Assert.Equal(SqlDbType.NVarChar, TypeOf(p)));
    }

    [Fact]
    public void Range_IsHalfOpenSoConsecutiveRangesTile()
    {
        var scope = SegmentScope.Build(MsSqlDialect.Instance, MsSqlValueBinding.Instance, new RangeSegment("OrderId", "1", "1000"), Columns);

        Assert.Equal("[OrderId] >= @segMin AND [OrderId] < @segMax", scope.Predicate);
        Assert.Equal([1, 1000], scope.Parameters.Select(p => p.Value));
        Assert.All(scope.Parameters, p => Assert.Equal(SqlDbType.Int, TypeOf(p)));
    }

    [Fact]
    public void Bounds_AreBoundAsTheColumnsOwnType_NotAsStrings()
    {
        var dates = SegmentScope.Build(MsSqlDialect.Instance, MsSqlValueBinding.Instance, 
            new RangeSegment("OrderDate", "2024-01-01T00:00:00.0000000", "2024-02-01T00:00:00.0000000"), Columns);

        Assert.All(dates.Parameters, p => Assert.Equal(SqlDbType.DateTime2, TypeOf(p)));
        Assert.Equal(new DateTime(2024, 1, 1), dates.Parameters[0].Value);
        Assert.Equal(new DateTime(2024, 2, 1), dates.Parameters[1].Value);
    }

    /// <summary>A SqlDbType.Decimal parameter left at the default scale of 0 truncates the fraction —
    /// a range boundary quietly becoming a different boundary rather than an error.</summary>
    [Fact]
    public void DecimalBounds_CarryTheirScale()
    {
        var scope = SegmentScope.Build(MsSqlDialect.Instance, MsSqlValueBinding.Instance, new RangeSegment("Amount", "10.25", "99.75"), Columns);

        Assert.Equal(10.25m, scope.Parameters[0].Value);
        Assert.Equal(2, scope.Parameters[0].Scale);
        Assert.Equal(99.75m, scope.Parameters[1].Value);
    }

    [Fact]
    public void SegmentColumn_IsTranslatedThroughColumnMappingsForTheTargetSide()
    {
        List<ColumnMapping> mappings = [new() { SourceColumn = "SourceRegion", TargetColumn = "Region" }];

        var scope = SegmentScope.Build(MsSqlDialect.Instance, MsSqlValueBinding.Instance, new ListSegment("SourceRegion", ["EU"]), Columns, mappings);

        Assert.Equal("[Region] IN (@seg0)", scope.Predicate);
    }

    [Fact]
    public void SegmentColumn_FallsBackToItsOwnNameWhenNoMappingRenamesIt()
    {
        List<ColumnMapping> mappings = [new() { SourceColumn = "Amount", TargetColumn = "Amount" }];

        var scope = SegmentScope.Build(MsSqlDialect.Instance, MsSqlValueBinding.Instance, new ListSegment("Region", ["EU"]), Columns, mappings);

        Assert.Equal("[Region] IN (@seg0)", scope.Predicate);
    }

    [Fact]
    public void UnknownColumn_IsRejectedWithTheAvailableColumnsListed()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => SegmentScope.Build(MsSqlDialect.Instance, MsSqlValueBinding.Instance, new ListSegment("Nope", ["x"]), Columns));

        Assert.Contains("'Nope' was not found", ex.Message);
        Assert.Contains("OrderId", ex.Message);
    }

    /// <summary>An empty IN list isn't valid SQL, and treating it as "matches nothing" would make a
    /// reconciling writer delete the target's whole scope.</summary>
    [Fact]
    public void EmptyList_IsRejected()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => SegmentScope.Build(MsSqlDialect.Instance, MsSqlValueBinding.Instance, new ListSegment("Region", []), Columns));

        Assert.Contains("no values", ex.Message);
    }

    [Fact]
    public void UnexpandedAutoSegment_IsRejectedRatherThanGuessedAt()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => SegmentScope.Build(MsSqlDialect.Instance, MsSqlValueBinding.Instance, new AutoSegment("OrderId", 4), Columns));

        Assert.Contains("unexpanded", ex.Message);
    }

    [Fact]
    public void MalformedBound_NamesTheColumnAndItsType()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => SegmentScope.Build(MsSqlDialect.Instance, MsSqlValueBinding.Instance, new RangeSegment("OrderId", "not-a-number", "10"), Columns));

        Assert.Contains("'OrderId'", ex.Message);
        Assert.Contains("int", ex.Message);
    }
}
