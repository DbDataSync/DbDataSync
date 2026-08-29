using DataSync.Drivers.MsSql;

namespace DataSync.Drivers.MsSql.Tests;

/// <summary>
/// The two parts of a CDC read with real defects available in them: the select list, and the boundary
/// arithmetic. Both are assertable without a live SQL Server, which is why the statement builder is
/// separate from the reader.
/// </summary>
public sealed class MsSqlCdcStatementTests
{
    private static string Read(
        MsSqlCdcStatement.CdcFunction function = MsSqlCdcStatement.CdcFunction.NetChanges,
        params string[] columns) =>
        MsSqlCdcStatement.BuildRead("dbo_Orders", function, columns.Length == 0 ? ["Id", "Name"] : columns);

    /// <summary>
    /// The off-by-one this exists to prevent. CDC's window functions are inclusive of `@from`, so
    /// passing the stored LSN unchanged re-reads the last pass's final change every single pass —
    /// harmless to the data, and enough to make a monitoring graph say a quiet source is busy.
    /// </summary>
    [Fact]
    public void TheLowerBound_IsIncremented()
    {
        Assert.Contains("sys.fn_cdc_increment_lsn(@storedLsn)", Read());
        Assert.DoesNotContain("(@storedLsn, @toLsn", Read());
    }

    [Fact]
    public void NetChanges_IsPreferredWhenAvailable() =>
        Assert.Contains("cdc.fn_cdc_get_net_changes_dbo_Orders(", Read());

    [Fact]
    public void AllChanges_IsTheFallbackName() =>
        Assert.Contains(
            "cdc.fn_cdc_get_all_changes_dbo_Orders(",
            Read(MsSqlCdcStatement.CdcFunction.AllChanges));

    /// <summary>The operation is the first column, and the reader reads it by that ordinal.</summary>
    [Fact]
    public void TheOperationIsSelectedFirst()
    {
        var sql = Read();
        Assert.Contains("SELECT __$operation, [Id], [Name]", sql);
        Assert.Equal(0, MsSqlCdcStatement.OperationOrdinal);
    }

    /// <summary>
    /// Ordered by the change's own position, so a window containing two changes to one key is applied
    /// in the order they happened — the difference between all-changes being usable and being a coin
    /// toss.
    /// <para>
    /// <c>__$seqval</c> orders changes *within* a transaction and only all-changes returns it: net
    /// changes has already collapsed them. Ordering by it unconditionally is an "Invalid column name"
    /// on the mode this reader prefers, which is what the integration tests found.
    /// </para>
    /// </summary>
    [Fact]
    public void AllChangesOrderWithinATransaction_AndNetChangesCannot()
    {
        Assert.Contains(
            "ORDER BY __$start_lsn, __$seqval", Read(MsSqlCdcStatement.CdcFunction.AllChanges));

        Assert.Contains("ORDER BY __$start_lsn;", Read());
        Assert.DoesNotContain("__$seqval", Read());
    }

    [Fact]
    public void ATransformIsRenderedAndAliasedBack()
    {
        var sql = MsSqlCdcStatement.BuildRead(
            "dbo_Orders", MsSqlCdcStatement.CdcFunction.NetChanges, ["Name"],
            column => $"UPPER([{column}]) AS [{column}]");

        Assert.Contains("UPPER([Name]) AS [Name]", sql);
    }

    [Theory]
    [InlineData(2, MsSqlCdcStatement.ChangeOperationCode.Insert)]
    [InlineData(4, MsSqlCdcStatement.ChangeOperationCode.Update)]
    [InlineData(1, MsSqlCdcStatement.ChangeOperationCode.Delete)]
    public void OperationCodes_MapToChanges(int code, MsSqlCdcStatement.ChangeOperationCode expected) =>
        Assert.Equal(expected, MsSqlCdcStatement.Operation(code));

    /// <summary>An update-before image can only arrive from a statement this reader did not build, so
    /// guessing at it would be worse than saying so.</summary>
    [Fact]
    public void AnUpdateBeforeImage_SaysItCameFromSomewhereElse() =>
        Assert.Contains(
            "never requests",
            Assert.Throws<InvalidOperationException>(() => MsSqlCdcStatement.Operation(3)).Message);

    [Fact]
    public void AnUnknownOperation_IsRefused() =>
        Assert.Throws<InvalidOperationException>(() => MsSqlCdcStatement.Operation(9));
}
