using DbDataSync.Core.Config;
using DbDataSync.Drivers.Abstractions;

namespace DbDataSync.Drivers.Generic.Tests;

/// <summary>
/// Phase 132's actual fix, as phase 145 rebuilt it: a CDC pass that stages more than one row for the
/// same key used to compute the identical pass-wide surrogate key for both and collide on the target's
/// own primary key. These pin the statement shapes <c>Scd2Writer</c> builds to avoid it — the
/// duplicate-key exclusion clause on the bulk statements, and the two set-based statements every
/// duplicate key in the batch now goes through together — all as text, the same way
/// <c>KeyReconcileScd2CloseStatementTests</c> pins its own statements without a server.
/// <para>
/// What a duplicate key's versions should come out as, rather than what the SQL looks like, is
/// <c>Scd2CdcGuaranteedDeliveryIntegrationTests</c>' job against a real server. These exist because the
/// window-function logic has real defects available in it that are readable in the text — comparing a
/// row against the wrong neighbour, excluding a delete from the ordering rather than only from the
/// insert — and catching those should not need CDC and a database.
/// </para>
/// </summary>
public sealed class Scd2WriterTests
{
    private static readonly List<ColumnMapping> Mappings =
    [
        new() { SourceColumn = "Id", TargetColumn = "Id" },
        new() { SourceColumn = "Name", TargetColumn = "Name" },
    ];

    [Fact]
    public void FindDuplicateKeys_GroupsByTheNaturalKeyAndKeepsOnlyThoseWithMoreThanOneRow()
    {
        Assert.Equal(
            "SELECT [Id]\nFROM #staging\nGROUP BY [Id]\nHAVING COUNT(*) > 1",
            HistorizedStatement.BuildFindDuplicateKeys(BracketDialect.Instance, "#staging", ["Id"]));
    }

    [Fact]
    public void FindDuplicateKeys_ACompositeKey_GroupsByEveryColumn()
    {
        var sql = HistorizedStatement.BuildFindDuplicateKeys(BracketDialect.Instance, "#staging", ["Region", "Id"]);

        Assert.Contains("SELECT [Region], [Id]", sql);
        Assert.Contains("GROUP BY [Region], [Id]", sql);
    }

    /// <summary>A self-referencing count, not a list of literal key values — the bulk statement's own
    /// shape never depends on how many keys happened to collide this pass.</summary>
    [Fact]
    public void DuplicateKeyExclusion_IsASelfJoinedCount_NotAListOfLiteralKeys()
    {
        var sql = HistorizedStatement.BuildDuplicateKeyExclusion(BracketDialect.Instance, "#staging", ["Id"]);

        Assert.Equal("(SELECT COUNT(*) FROM #staging dup WHERE dup.[Id] = s.[Id]) = 1", sql);
    }

    [Fact]
    public void DuplicateKeyExclusion_ACompositeKey_MatchesOnEveryColumn()
    {
        var sql = HistorizedStatement.BuildDuplicateKeyExclusion(BracketDialect.Instance, "#staging", ["Region", "Id"]);

        Assert.Contains("dup.[Region] = s.[Region] AND dup.[Id] = s.[Id]", sql);
    }

    // ---- BuildCloseChanged: the bulk statement's duplicate-key exclusion --------------------------

    [Fact]
    public void CloseChanged_WithAStagingFilter_AddsItToTheExistsClause()
    {
        var sql = HistorizedStatement.BuildCloseChanged(
            BracketDialect.Instance, "[dbo].[Hist]", "#staging", ["Id"], ["Name"],
            stagingFilter: "(SELECT COUNT(*) FROM #staging dup WHERE dup.[Id] = s.[Id]) = 1");

        Assert.Contains(
            "AND (SELECT COUNT(*) FROM #staging dup WHERE dup.[Id] = s.[Id]) = 1", sql);
        // Still the pass-wide @now: a staging filter alone (no validToExpression) does not change what
        // closes the version, only which staged rows are allowed to.
        Assert.Contains("[DS_ValidTo] = @now", sql);
    }

    /// <summary>No filter at all — the default — is the exact statement every pairing but CDC still
    /// gets, unchanged since before phase 132.</summary>
    [Fact]
    public void CloseChanged_WithNoStagingFilterOrValidToExpression_IsByteForByteUnchanged()
    {
        Assert.Equal(
            HistorizedStatement.BuildCloseChanged(BracketDialect.Instance, "[dbo].[Hist]", "#staging", ["Id"], ["Name"]),
            HistorizedStatement.BuildCloseChanged(
                BracketDialect.Instance, "[dbo].[Hist]", "#staging", ["Id"], ["Name"],
                validToExpression: null, stagingFilter: null));
    }

    /// <summary>
    /// A real per-row source time closes the version instead of the pass-wide <c>@now</c> — a
    /// correlated scalar subquery, since the <c>SET</c> clause has no row alias of its own to read a
    /// staged column from. Safe as a scalar subquery because the only caller that passes one is the
    /// bulk statement, whose own staging filter leaves it at most one staged row per key.
    /// </summary>
    [Fact]
    public void CloseChanged_WithAValidToExpression_ReadsItFromACorrelatedSubquery()
    {
        var sql = HistorizedStatement.BuildCloseChanged(
            BracketDialect.Instance, "[dbo].[Hist]", "#staging", ["Id"], ["Name"],
            validToExpression: "s.[__DS_ChangedAtUtc]",
            stagingFilter: "(SELECT COUNT(*) FROM #staging dup WHERE dup.[Id] = s.[Id]) = 1");

        Assert.Contains(
            "SET [DS_ValidTo] = (SELECT s.[__DS_ChangedAtUtc] FROM #staging s WHERE s.[Id] = [dbo].[Hist].[Id]", sql);
        Assert.DoesNotContain("@now", sql);
    }

    // ---- BuildOpenVersions: still the bulk statement, minus phase 132's per-row scoping -----------

    [Fact]
    public void OpenVersions_WithAStagingFilter_AddsItRightAfterTheDeleteCheck()
    {
        var sql = HistorizedStatement.BuildOpenVersions(
            BracketDialect.Instance, "[dbo].[Hist]", "#staging", ["Id"], Mappings,
            stagingFilter: "(SELECT COUNT(*) FROM #staging dup WHERE dup.[Id] = s.[Id]) = 1");

        Assert.Contains(
            "WHERE s.[__Operation] <> 'D' AND (SELECT COUNT(*) FROM #staging dup WHERE dup.[Id] = s.[Id]) = 1",
            sql);
    }

    [Fact]
    public void OpenVersions_WithNoOptionalArguments_IsByteForByteUnchanged()
    {
        Assert.Equal(
            HistorizedStatement.BuildOpenVersions(BracketDialect.Instance, "[dbo].[Hist]", "#staging", ["Id"], Mappings),
            HistorizedStatement.BuildOpenVersions(
                BracketDialect.Instance, "[dbo].[Hist]", "#staging", ["Id"], Mappings,
                validFromExpression: null, stagingFilter: null));
    }

    /// <summary>A real per-row source time opens the version at instead of the pass-wide <c>@now</c> —
    /// no correlated subquery needed here, unlike <c>BuildCloseChanged</c>'s <c>ValidTo</c>: the
    /// statement already selects per staged row, so each row carries its own value straight through.</summary>
    [Fact]
    public void OpenVersions_WithAValidFromExpression_SelectsItInsteadOfNow()
    {
        var sql = HistorizedStatement.BuildOpenVersions(
            BracketDialect.Instance, "[dbo].[Hist]", "#staging", ["Id"], Mappings,
            validFromExpression: "s.[__DS_ChangedAtUtc]");

        Assert.Contains("s.[__DS_ChangedAtUtc], TRUE", sql);
        Assert.DoesNotContain("@now", sql);
    }

    /// <summary>The bulk statement still uses the pass-wide prefix — phase 145 took the per-row
    /// override away with the loop that needed it, and nothing else ever passed one.</summary>
    [Fact]
    public void OpenVersions_StillUsesThePassWidePrefix()
    {
        var sql = HistorizedStatement.BuildOpenVersions(
            BracketDialect.Instance, "[dbo].[Hist]", "#staging", ["Id"], Mappings);

        Assert.Contains("@versionKeyPrefix || CAST(s.[Id] AS VARCHAR(4000))", sql);
    }

    // ---- Phase 145: the two set-based duplicate-key statements ------------------------------------

    private static string OpenDuplicates(IReadOnlyList<string> keys, IReadOnlyList<string> values) =>
        HistorizedStatement.BuildOpenDuplicateKeyVersions(
            BracketDialect.Instance, "[dbo].[Hist]", "#staging", keys, values, Mappings);

    private static string CloseDuplicates(IReadOnlyList<string> keys, IReadOnlyList<string> values) =>
        HistorizedStatement.BuildCloseDuplicateKeyVersions(
            BracketDialect.Instance, "[dbo].[Hist]", "#staging", keys, values, Mappings);

    /// <summary>
    /// The three window functions the rewrite rests on, each partitioned by the natural key and — for
    /// the two that are order-sensitive — ordered by the column that is unique per row in true source
    /// order. A <c>COUNT</c> is what makes finding the duplicate keys free; a <c>LAG</c> is what lets a
    /// row be compared against the version open before it without asking the target; a <c>LEAD</c> is
    /// what gives a version its end.
    /// </summary>
    [Fact]
    public void TheDerivedTable_PartitionsByTheKeyAndOrdersByTheOrderingColumn()
    {
        var sql = OpenDuplicates(["Id"], ["Name"]);

        Assert.Contains("COUNT(*) OVER (PARTITION BY s.[Id]) AS __DS_KeyCount", sql);
        Assert.Contains(
            "ROW_NUMBER() OVER (PARTITION BY s.[Id] ORDER BY s.[__DS_ChangeOrdering]) AS __DS_Rn", sql);
        Assert.Contains(
            "LAG(s.[Name]) OVER (PARTITION BY s.[Id] ORDER BY s.[__DS_ChangeOrdering]) AS __DS_Prev0", sql);
        Assert.Contains(
            "LEAD(m.__DS_ChangedAt) OVER (PARTITION BY m.[Id] ORDER BY m.__DS_Ordering) AS __DS_NextChangedAt",
            sql);
    }

    /// <summary>Only keys with more than one staged row — the singleton keys are the bulk statements'
    /// to carry, exactly as before, and the count is computed inline rather than fetched back.</summary>
    [Fact]
    public void TheDerivedTable_KeepsOnlyKeysWithMoreThanOneStagedRow()
    {
        Assert.Contains("WHERE d.__DS_KeyCount > 1", OpenDuplicates(["Id"], ["Name"]));
    }

    /// <summary>
    /// The whole reason the loop could become two statements: a row's predecessor *is* the version open
    /// when it arrives, so only the first row of a key has to look at the target at all. Getting this
    /// backwards — comparing every row against the target — would reopen phase 132's collision.
    /// </summary>
    [Fact]
    public void OnlyTheFirstRowOfAKey_ConsultsTheTarget()
    {
        var sql = OpenDuplicates(["Id"], ["Name"]);

        Assert.Contains("WHEN d.__DS_Rn = 1 THEN", sql);
        Assert.Contains("SELECT 1 FROM [dbo].[Hist] t", sql);
        // Every other row compares against the row before it, null-safely, and against nothing else.
        Assert.Contains(
            "CASE WHEN d.__DS_PrevOp = 'D' OR ((CASE WHEN d.__DS_Prev0 IS NULL THEN 1 ELSE 0 END " +
            "<> CASE WHEN d.[Name] IS NULL THEN 1 ELSE 0 END OR (d.__DS_Prev0 IS NOT NULL AND " +
            "d.[Name] IS NOT NULL AND d.__DS_Prev0 <> d.[Name])))",
            sql);
    }

    /// <summary>A delete leaves no open version behind, so whatever follows one opens regardless of its
    /// values — the row-by-row loop got this from its own state, and here it is an explicit test on the
    /// previous row's operation.</summary>
    [Fact]
    public void ARowAfterADelete_OpensWhateverItsValues()
    {
        Assert.Contains("d.__DS_PrevOp = 'D' OR", OpenDuplicates(["Id"], ["Name"]));
    }

    /// <summary>
    /// A delete opens nothing, and is still in the ordering. Both halves matter and they pull in
    /// opposite directions: filtering deletes out of the derived table would lose what ends the version
    /// before them, and letting them through the insert would open a version for a row that says the
    /// source row is gone.
    /// </summary>
    [Fact]
    public void ADelete_IsABoundaryButOpensNothing()
    {
        var sql = OpenDuplicates(["Id"], ["Name"]);

        Assert.Contains("WHERE m.__DS_Opens = 1 OR m.__DS_Op = 'D'", sql);
        Assert.Contains("CASE WHEN d.__DS_Op <> 'D' AND", sql);
        Assert.Contains("WHERE b.__DS_Opens = 1", sql);
    }

    /// <summary>
    /// Unlike the bulk statement, this one writes <c>ValidTo</c> and derives <c>IsCurrent</c> from it:
    /// a version ends where the next boundary begins, and is the current one exactly when there is no
    /// next boundary. A pass that ends in a delete therefore opens its last version already closed.
    /// </summary>
    [Fact]
    public void AVersionEndsAtTheNextBoundary_AndIsCurrentOnlyWhenThereIsNone()
    {
        var sql = OpenDuplicates(["Id"], ["Name"]);

        Assert.Contains("[DS_VersionKey], [Id], [Name], [DS_ValidFrom], [DS_ValidTo], [DS_IsCurrent]", sql);
        Assert.Contains(
            "b.__DS_ChangedAt, b.__DS_NextChangedAt, CASE WHEN b.__DS_NextChangedAt IS NULL THEN TRUE ELSE FALSE END",
            sql);
    }

    /// <summary>Phase 132's surrogate key, unchanged: the staged row's own ordering value, which is
    /// unique per row by construction, rather than the pass-wide prefix a singleton key still gets.</summary>
    [Fact]
    public void TheSurrogateKey_IsStillTheOrderingValue_NotThePassWidePrefix()
    {
        var sql = OpenDuplicates(["Id"], ["Name"]);

        Assert.Contains("b.__DS_Ordering || '|' || CAST(b.[Id] AS VARCHAR(4000))", sql);
        Assert.DoesNotContain("@versionKeyPrefix", sql);
    }

    /// <summary>The real SQL Server dialect concatenates with <c>+</c>, not the generic test double's
    /// ANSI <c>||</c> — confirmed against the actual dialect <c>Scd2Writer</c> runs with on MsSql, the
    /// only engine that produces an ordered batch today.</summary>
    [Fact]
    public void TheSurrogateKey_AgainstTheRealMsSqlDialect_ConcatenatesWithPlus()
    {
        var sql = HistorizedStatement.BuildOpenDuplicateKeyVersions(
            DbDataSync.Core.Sql.MsSqlDialect.Instance, "[dbo].[Hist]", "#staging", ["Id"], ["Name"], Mappings);

        Assert.Contains("b.__DS_Ordering + '|' + CAST(b.[Id] AS VARCHAR(4000))", sql);
    }

    /// <summary>
    /// The close ends the version the key already had open at the *first boundary*, not the first
    /// staged row: a leading change that touches no mapped column is not a transition, and ending a
    /// version at one would close something nothing had replaced.
    /// </summary>
    [Fact]
    public void TheClose_EndsTheOpenVersionAtTheFirstBoundary()
    {
        var sql = CloseDuplicates(["Id"], ["Name"]);

        Assert.Contains(
            "SET [DS_ValidTo] =\n        (SELECT b.__DS_ChangedAt FROM __DS_Boundary b " +
            "WHERE b.[Id] = [dbo].[Hist].[Id] AND b.__DS_BoundaryRn = 1)",
            sql);
        Assert.Contains("[DS_IsCurrent] = FALSE", sql);
        Assert.Contains("WHERE [DS_IsCurrent] = TRUE", sql);
    }

    /// <summary>
    /// Both statements read "what was open for this key before this pass," and the second one runs
    /// against a target the first has already written to — so both exclude the rows this pass opened,
    /// by the surrogate key it computed for them. Without it the two renderings of the derived table
    /// would disagree, and the close would end versions the open had just created.
    /// </summary>
    [Fact]
    public void BothStatements_IgnoreTheVersionsThisPassItselfOpened()
    {
        const string exclusion =
            "NOT EXISTS (SELECT 1 FROM #staging p WHERE t.[DS_VersionKey] = " +
            "p.[__DS_ChangeOrdering] || '|' || CAST(p.[Id] AS VARCHAR(4000)))";

        Assert.Contains(exclusion, OpenDuplicates(["Id"], ["Name"]));
        Assert.Contains(exclusion, CloseDuplicates(["Id"], ["Name"]));
        // The close also has to keep them out of the rows it is updating, not merely out of the
        // lookup — they are IsCurrent by then, and every one of them would otherwise match.
        Assert.Contains(
            "NOT EXISTS (SELECT 1 FROM #staging p WHERE [dbo].[Hist].[DS_VersionKey] = " +
            "p.[__DS_ChangeOrdering] || '|' || CAST(p.[Id] AS VARCHAR(4000)))",
            CloseDuplicates(["Id"], ["Name"]));
    }

    /// <summary>A composite natural key partitions, joins and hashes on every part — the place a
    /// single-column assumption would hide.</summary>
    [Fact]
    public void ACompositeNaturalKey_PartitionsAndJoinsOnEveryPart()
    {
        var sql = HistorizedStatement.BuildOpenDuplicateKeyVersions(
            BracketDialect.Instance, "[dbo].[Hist]", "#staging", ["Region", "Id"], ["Name"],
            [
                new() { SourceColumn = "Region", TargetColumn = "Region" },
                new() { SourceColumn = "Id", TargetColumn = "Id" },
                new() { SourceColumn = "Name", TargetColumn = "Name" },
            ]);

        Assert.Contains("PARTITION BY s.[Region], s.[Id] ORDER BY s.[__DS_ChangeOrdering]", sql);
        Assert.Contains("t.[Region] = d.[Region] AND t.[Id] = d.[Id]", sql);
        Assert.Contains("PARTITION BY m.[Region], m.[Id] ORDER BY m.__DS_Ordering", sql);
        Assert.Contains(
            "b.__DS_Ordering || '|' || CAST(b.[Region] AS VARCHAR(4000)) || '|' || CAST(b.[Id] AS VARCHAR(4000))",
            sql);
    }

    /// <summary>
    /// A table whose every mapped column is part of the key has nothing that can change, so no row ever
    /// differs from its predecessor and only a delete is a boundary — the same <c>1 = 0</c> the bulk
    /// close already renders for the same reason, rather than an empty <c>OR</c> that would not parse.
    /// </summary>
    [Fact]
    public void WithNoValueColumns_OnlyADeleteIsABoundary()
    {
        var sql = HistorizedStatement.BuildOpenDuplicateKeyVersions(
            BracketDialect.Instance, "[dbo].[Hist]", "#staging", ["Id"], [],
            [new() { SourceColumn = "Id", TargetColumn = "Id" }]);

        Assert.Contains("CASE WHEN d.__DS_PrevOp = 'D' OR (1 = 0) THEN 1 ELSE 0 END", sql);
        Assert.Contains("AND NOT (1 = 0)", sql);
        Assert.DoesNotContain("__DS_Prev0", sql);
    }

    /// <summary>Engine-neutral, which is the claim putting these in the generic layer makes: nothing
    /// here is spelled SQL Server's way when another dialect is asked for it.</summary>
    [Fact]
    public void TheSameStatementsAreBuiltForAnotherDialect()
    {
        var sql = HistorizedStatement.BuildOpenDuplicateKeyVersions(
            ColonDialect.Instance, "\"public\".\"hist\"", "staging", ["id"], ["name"],
            [
                new() { SourceColumn = "id", TargetColumn = "id" },
                new() { SourceColumn = "name", TargetColumn = "name" },
            ]);

        Assert.Contains("\"DS_VersionKey\", \"id\", \"name\", \"DS_ValidFrom\", \"DS_ValidTo\", \"DS_IsCurrent\"", sql);
        Assert.Contains("LAG(s.\"name\") OVER (PARTITION BY s.\"id\" ORDER BY s.\"__DS_ChangeOrdering\")", sql);
        Assert.DoesNotContain("[", sql);
    }
}
