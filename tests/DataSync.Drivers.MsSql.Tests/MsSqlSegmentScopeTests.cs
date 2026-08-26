using System.Data;
using DataSync.Core.Config;
using DataSync.Drivers.Abstractions;
using Xunit;

namespace DataSync.Drivers.MsSql.Tests;

/// <summary>
/// Predicate rendering and bound binding, without a live SQL Server — this is the layer where a
/// segment stops being a description and becomes SQL, so it's worth pinning exactly.
/// </summary>
public sealed class MsSqlSegmentScopeTests
{
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
        var scope = MsSqlSegmentScope.Build(new FullSegment(), Columns);

        Assert.Equal("1 = 1", scope.Predicate);
        Assert.Empty(scope.Parameters);
    }

    [Fact]
    public void NoSegment_RendersTheSameAsFull()
    {
        Assert.Equal("1 = 1", MsSqlSegmentScope.Build(null, Columns).Predicate);
    }

    [Fact]
    public void List_RendersAnInClauseOfTypedParameters()
    {
        var scope = MsSqlSegmentScope.Build(new ListSegment("Region", ["EU", "US", "APAC"]), Columns);

        Assert.Equal("[Region] IN (@__seg0, @__seg1, @__seg2)", scope.Predicate);
        Assert.Equal(["EU", "US", "APAC"], scope.Parameters.Select(p => p.Value));
        Assert.All(scope.Parameters, p => Assert.Equal(SqlDbType.NVarChar, p.SqlDbType));
    }

    [Fact]
    public void Range_IsHalfOpenSoConsecutiveRangesTile()
    {
        var scope = MsSqlSegmentScope.Build(new RangeSegment("OrderId", "1", "1000"), Columns);

        Assert.Equal("[OrderId] >= @__segMin AND [OrderId] < @__segMax", scope.Predicate);
        Assert.Equal([1, 1000], scope.Parameters.Select(p => p.Value));
        Assert.All(scope.Parameters, p => Assert.Equal(SqlDbType.Int, p.SqlDbType));
    }

    [Fact]
    public void Bounds_AreBoundAsTheColumnsOwnType_NotAsStrings()
    {
        var dates = MsSqlSegmentScope.Build(
            new RangeSegment("OrderDate", "2024-01-01T00:00:00.0000000", "2024-02-01T00:00:00.0000000"), Columns);

        Assert.All(dates.Parameters, p => Assert.Equal(SqlDbType.DateTime2, p.SqlDbType));
        Assert.Equal(new DateTime(2024, 1, 1), dates.Parameters[0].Value);
        Assert.Equal(new DateTime(2024, 2, 1), dates.Parameters[1].Value);
    }

    /// <summary>A SqlDbType.Decimal parameter left at the default scale of 0 truncates the fraction —
    /// a range boundary quietly becoming a different boundary rather than an error.</summary>
    [Fact]
    public void DecimalBounds_CarryTheirScale()
    {
        var scope = MsSqlSegmentScope.Build(new RangeSegment("Amount", "10.25", "99.75"), Columns);

        Assert.Equal(10.25m, scope.Parameters[0].Value);
        Assert.Equal(2, scope.Parameters[0].Scale);
        Assert.Equal(99.75m, scope.Parameters[1].Value);
    }

    [Fact]
    public void SegmentColumn_IsTranslatedThroughColumnMappingsForTheTargetSide()
    {
        List<ColumnMapping> mappings = [new() { SourceColumn = "SourceRegion", TargetColumn = "Region" }];

        var scope = MsSqlSegmentScope.Build(new ListSegment("SourceRegion", ["EU"]), Columns, mappings);

        Assert.Equal("[Region] IN (@__seg0)", scope.Predicate);
    }

    [Fact]
    public void SegmentColumn_FallsBackToItsOwnNameWhenNoMappingRenamesIt()
    {
        List<ColumnMapping> mappings = [new() { SourceColumn = "Amount", TargetColumn = "Amount" }];

        var scope = MsSqlSegmentScope.Build(new ListSegment("Region", ["EU"]), Columns, mappings);

        Assert.Equal("[Region] IN (@__seg0)", scope.Predicate);
    }

    [Fact]
    public void UnknownColumn_IsRejectedWithTheAvailableColumnsListed()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => MsSqlSegmentScope.Build(new ListSegment("Nope", ["x"]), Columns));

        Assert.Contains("'Nope' was not found", ex.Message);
        Assert.Contains("OrderId", ex.Message);
    }

    /// <summary>An empty IN list isn't valid SQL, and treating it as "matches nothing" would make a
    /// reconciling writer delete the target's whole scope.</summary>
    [Fact]
    public void EmptyList_IsRejected()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => MsSqlSegmentScope.Build(new ListSegment("Region", []), Columns));

        Assert.Contains("no values", ex.Message);
    }

    [Fact]
    public void UnexpandedAutoSegment_IsRejectedRatherThanGuessedAt()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => MsSqlSegmentScope.Build(new AutoSegment("OrderId", 4), Columns));

        Assert.Contains("unexpanded", ex.Message);
    }

    [Fact]
    public void MalformedBound_NamesTheColumnAndItsType()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => MsSqlSegmentScope.Build(new RangeSegment("OrderId", "not-a-number", "10"), Columns));

        Assert.Contains("'OrderId'", ex.Message);
        Assert.Contains("int", ex.Message);
    }
}
