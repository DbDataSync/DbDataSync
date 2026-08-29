using DataSync.Drivers.Generic;

namespace DataSync.Drivers.Generic.Tests;

/// <summary>
/// The read against a trigger-maintained shadow table, which is the engine-neutral half of this
/// mechanism — one reader for every engine that has triggers. Two things in it have real defects
/// available, and both are here rather than only in an integration test: the collapsing, which decides
/// whether this is usable under load, and where the key comes from, which decides whether a delete
/// works at all.
/// </summary>
public sealed class TriggerAuditStatementTests
{
    private static string Read(
        IReadOnlyList<string>? keys = null,
        IReadOnlyList<string>? nonKeys = null,
        Func<string, string>? render = null) =>
        TriggerAuditStatement.BuildRead(
            BracketDialect.Instance, "dbo", "Orders",
            keys ?? ["Id"], nonKeys ?? ["Name", "Total"], render);

    /// <summary>
    /// A shadow table records every write, so a row updated fifty times between passes is fifty rows.
    /// Staging fifty versions to write the last one is the difference between this mechanism being
    /// usable on a busy table and not.
    /// </summary>
    [Fact]
    public void ChangesAreCollapsedToTheNetChangePerKey()
    {
        var sql = Read();

        Assert.Contains("MAX([DS_Seq])", sql);
        Assert.Contains("GROUP BY [Id]", sql);
        Assert.Contains("JOIN (", sql);
    }

    /// <summary>
    /// The phase 12 bug, which cost a failed run and a long diagnosis: the select list re-emitted the
    /// key from the joined base row, and for a row deleted since capture the LEFT JOIN made it NULL —
    /// destroying the one value still reliable. The shadow row always has the key; the base row may
    /// not exist.
    /// </summary>
    [Fact]
    public void TheKeyComesFromTheShadowRowAndNeverFromTheBaseRow()
    {
        var sql = Read();

        Assert.Contains("c.[Id]", sql);
        Assert.DoesNotContain("base.[Id],", sql);
        Assert.DoesNotContain("base.*", sql);
    }

    /// <summary>Composite keys fall out of the same shape — the sequence orders the feed, not the key,
    /// so nothing here depends on a key being single or orderable.</summary>
    [Fact]
    public void ACompositeKey_CollapsesAndJoinsOnEveryPart()
    {
        var sql = Read(keys: ["Region", "Id"], nonKeys: ["Name"]);

        Assert.Contains("GROUP BY [Region], [Id]", sql);
        Assert.Contains("c.[Region] = base.[Region] AND c.[Id] = base.[Id]", sql);
    }

    /// <summary>Bounded at both ends, so a change written while the pass is reading is next pass's
    /// work rather than a row read twice or a position that skips it.</summary>
    [Fact]
    public void TheWindowIsBoundedAtBothEnds()
    {
        var sql = Read();

        Assert.Contains("[DS_Seq] > @previousSequence AND [DS_Seq] <= @targetSequence", sql);
        Assert.Contains("ORDER BY c.[DS_Seq]", sql);
    }

    /// <summary>Tested against a key column, which cannot be null in the shadow row — so NULL can only
    /// mean the join matched nothing, never that the base row holds a NULL.</summary>
    [Fact]
    public void TheMissingBaseRowMarker_TestsAKeyColumn()
    {
        var sql = Read();

        Assert.Contains("CASE WHEN base.[Id] IS NULL THEN 1 ELSE 0 END AS [DS_BaseMissing]", sql);
        Assert.Equal(0, TriggerAuditStatement.OperationOrdinal);
        Assert.Equal(1, TriggerAuditStatement.BaseMissingOrdinal);
        Assert.Equal(2, TriggerAuditStatement.FirstKeyOrdinal);
    }

    [Fact]
    public void ATransformIsRenderedAndAliasedBack()
    {
        var sql = Read(nonKeys: ["Name"], render: c => $"UPPER(base.[{c}]) AS [{c}]");

        Assert.Contains("UPPER(base.[Name]) AS [Name]", sql);
    }

    /// <summary>
    /// Without a key there is nothing to collapse by and nothing to identify a deleted row with, so
    /// this is refused where it can be explained rather than producing SQL that fails at the source.
    /// </summary>
    [Fact]
    public void ATableWithNoKey_IsRefusedWithAReason() =>
        Assert.Contains(
            "has none",
            Assert.Throws<InvalidOperationException>(() => Read(keys: [])).Message);

    /// <summary>
    /// The same statement through a second dialect, because "engine-neutral" is the claim this
    /// mechanism is built on — and a hardcoded quote character would pass every test above.
    /// </summary>
    [Fact]
    public void TheSameReadIsBuiltForADialectThatQuotesAndBindsDifferently()
    {
        var sql = TriggerAuditStatement.BuildRead(
            ColonDialect.Instance, "public", "orders", ["id"], ["name"]);

        Assert.Contains("\"public\".\"DS_Changes_orders\"", sql);
        Assert.Contains("\"DS_Seq\" > :previousSequence AND \"DS_Seq\" <= :targetSequence", sql);
        Assert.Contains("c.\"id\"", sql);
        Assert.DoesNotContain("[", sql);
    }

    [Fact]
    public void TheShadowTableSitsBesideItsSource() =>
        Assert.Equal("DS_Changes_Orders", TriggerAuditStatement.ShadowTableName("Orders"));

    /// <summary>At or below the acknowledged position, never above it — the row at the watermark is
    /// the one a re-read starts after.</summary>
    [Fact]
    public void PruningDeletesOnlyWhatHasBeenApplied()
    {
        var sql = TriggerAuditStatement.BuildPrune(BracketDialect.Instance, "dbo", "Orders");

        Assert.Contains("DELETE FROM [dbo].[DS_Changes_Orders]", sql);
        Assert.Contains("[DS_Seq] <= @throughSequence", sql);
    }
}
