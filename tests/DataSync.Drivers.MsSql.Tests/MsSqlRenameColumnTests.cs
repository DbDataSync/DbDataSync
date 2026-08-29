using DataSync.Drivers.MsSql;

namespace DataSync.Drivers.MsSql.Tests;

/// <summary>
/// SQL Server has no <c>ALTER TABLE … RENAME COLUMN</c>. The two mistakes this pins are both silent:
/// passing the new name qualified leaves a column literally called <c>dbo.T.NewName</c>, and quoting
/// the old name wrong renames nothing while reporting success.
/// </summary>
public sealed class MsSqlRenameColumnTests
{
    [Fact]
    public void TheOldNameIsQualified_AndTheNewOneIsNot()
    {
        var sql = MsSqlDialect.Instance.RenderRenameColumn("[dbo].[Orders]", "CustId", "CustomerId");

        Assert.Equal("EXEC sp_rename N'[dbo].[Orders].[CustId]', N'CustomerId', 'COLUMN';", sql);
    }

    /// <summary>A quote in an identifier would otherwise end the literal early and leave the rest of
    /// the name as SQL.</summary>
    [Fact]
    public void ASingleQuoteInAName_IsEscapedRatherThanEndingTheLiteral()
    {
        var sql = MsSqlDialect.Instance.RenderRenameColumn("[dbo].[Orders]", "O'Brien", "Name");

        Assert.Contains("O''Brien", sql);
        Assert.DoesNotContain("'O'Brien'", sql);
    }
}
