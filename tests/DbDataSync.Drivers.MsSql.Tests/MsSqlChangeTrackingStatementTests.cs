using Xunit;

namespace DbDataSync.Drivers.MsSql.Tests;

/// <summary>
/// The select list is the part that carried a real defect — it re-emitted the primary key via
/// <c>base.*</c>, so the reader's name-keyed column mapping overwrote CHANGETABLE's key with base's,
/// nulling it whenever the source row had been deleted. These assertions are about shape, so they
/// need no server.
/// </summary>
public sealed class MsSqlChangeTrackingStatementTests
{
    private static string Build(params string[] nonKeyColumns) =>
        MsSqlChangeTrackingStatement.BuildIncremental("dbo", "Orders", ["Id"], nonKeyColumns);

    [Fact]
    public void PrimaryKey_IsSelectedOnlyFromChangetable_NeverFromTheJoinedTable()
    {
        var sql = Build("Region", "Amount");

        Assert.Contains("CT.[Id]", sql);
        Assert.DoesNotContain("base.[Id],", sql);          // not in the select list
        Assert.DoesNotContain("base.*", sql);
        // The only base.[Id] reference left is the join condition and the missing-row test.
        Assert.Equal(2, sql.Split("base.[Id]").Length - 1);
    }

    [Fact]
    public void NonKeyColumns_AreNamedExplicitly()
    {
        var sql = Build("Region", "Amount");

        Assert.Contains("base.[Region]", sql);
        Assert.Contains("base.[Amount]", sql);
    }

    /// <summary>The marker is tested against a key column, which is NOT NULL in the source — so NULL
    /// can only mean the LEFT JOIN matched nothing, never a real NULL value.</summary>
    [Fact]
    public void MissingRowMarker_TestsAKeyColumn()
    {
        var sql = Build("Region");

        Assert.Contains("CASE WHEN base.[Id] IS NULL THEN 1 ELSE 0 END AS __BaseMissing", sql);
    }

    [Fact]
    public void ColumnOrder_MatchesTheOrdinalsTheReaderWalks()
    {
        var sql = Build("Region", "Amount");
        var selectList = sql[sql.IndexOf("SELECT ", StringComparison.Ordinal)..sql.IndexOf("\nFROM", StringComparison.Ordinal)];
        var columns = selectList["SELECT ".Length..].Split(", ");

        Assert.Equal("CT.SYS_CHANGE_OPERATION", columns[MsSqlChangeTrackingStatement.OperationOrdinal]);
        Assert.StartsWith("CASE WHEN", columns[MsSqlChangeTrackingStatement.BaseMissingOrdinal]);
        Assert.Equal("CT.[Id]", columns[MsSqlChangeTrackingStatement.FirstKeyOrdinal]);
        Assert.Equal("base.[Region]", columns[MsSqlChangeTrackingStatement.FirstKeyOrdinal + 1]);
    }

    [Fact]
    public void CompositeKey_JoinsAndSelectsEveryKeyColumn()
    {
        var sql = MsSqlChangeTrackingStatement.BuildIncremental("dbo", "Orders", ["TenantId", "Id"], ["Region"]);

        Assert.Contains("CT.[TenantId] = base.[TenantId] AND CT.[Id] = base.[Id]", sql);
        Assert.Contains("CT.[TenantId], CT.[Id]", sql);
        // The marker only needs one key column; any of them being NULL means the same thing.
        Assert.Contains("CASE WHEN base.[TenantId] IS NULL", sql);
    }

    /// <summary>A table whose every column belongs to the key still has to produce valid SQL — the
    /// non-key list is simply empty, with no dangling comma.</summary>
    [Fact]
    public void KeyOnlyTable_ProducesNoTrailingComma()
    {
        var sql = MsSqlChangeTrackingStatement.BuildIncremental("dbo", "Orders", ["Id"], []);

        Assert.Contains("CT.[Id]\nFROM", sql);
    }

    [Fact]
    public void VersionFilterAndOrdering_AreUnchanged()
    {
        var sql = Build("Region");

        Assert.Contains("WHERE CT.SYS_CHANGE_VERSION <= @targetVersion", sql);
        Assert.Contains("ORDER BY CT.SYS_CHANGE_VERSION", sql);
    }

    private static string BuildBounded(params string[] nonKeyColumns) =>
        MsSqlChangeTrackingStatement.BuildIncremental(
            "dbo", "Orders", ["Id"], nonKeyColumns, renderNonKeyColumn: null, bounded: true);

    [Fact]
    public void Bounded_CapsTheWindowWithTies()
    {
        var sql = BuildBounded("Region");

        Assert.Contains("SELECT TOP (@maxRows) WITH TIES ", sql);
    }

    [Fact]
    public void Bounded_KeepsTheVersionBoundRatherThanReplacingIt()
    {
        // The row cap narrows the window; it does not become the window. A pass still reads no further
        // than the end version it fixed for itself before it started.
        var sql = BuildBounded("Region");

        Assert.Contains("WHERE CT.SYS_CHANGE_VERSION <= @targetVersion", sql);
        Assert.Contains("ORDER BY CT.SYS_CHANGE_VERSION", sql);
    }

    [Fact]
    public void Bounded_PutsTheVersionLast_SoTheLeadingOrdinalsAreUnchanged()
    {
        // FirstKeyOrdinal and the reader's whole ordinal arithmetic depend on nothing being inserted
        // ahead of the key columns. The position column is bookkeeping and goes at the end.
        var sql = BuildBounded("Region", "Amount");

        Assert.Contains("base.[Amount], CT.SYS_CHANGE_VERSION AS [__DS_Position]\nFROM", sql);
    }

    [Fact]
    public void Unbounded_CarriesNoRowCapAndNoPositionColumn()
    {
        var sql = Build("Region");

        Assert.DoesNotContain("TOP", sql);
        Assert.DoesNotContain("__DS_Position", sql);
    }
}
