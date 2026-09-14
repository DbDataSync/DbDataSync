using DbDataSync.Core.Config;
using DbDataSync.Drivers.Abstractions;

namespace DbDataSync.Drivers.Generic.Tests;

/// <summary>
/// Phase 132's actual fix: a CDC pass that stages more than one row for the same key used to compute
/// the identical pass-wide surrogate key for both and collide on the target's own primary key. These
/// pin the statement shapes <c>Scd2Writer</c> builds to avoid it — the duplicate-key exclusion clause on
/// the bulk statements, and the single-row-scoped close/open statements a duplicate key is processed
/// through one row at a time — all as text, the same way <c>KeyReconcileScd2CloseStatementTests</c>
/// pins its own statements without a server.
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
            "SELECT [Id]\nFROM #staging\nGROUP BY [Id]\nHAVING COUNT(*) > 1;",
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

        Assert.Equal("(SELECT COUNT(*) FROM #staging AS dup WHERE dup.[Id] = s.[Id]) = 1", sql);
    }

    [Fact]
    public void DuplicateKeyExclusion_ACompositeKey_MatchesOnEveryColumn()
    {
        var sql = HistorizedStatement.BuildDuplicateKeyExclusion(BracketDialect.Instance, "#staging", ["Region", "Id"]);

        Assert.Contains("dup.[Region] = s.[Region] AND dup.[Id] = s.[Id]", sql);
    }

    [Fact]
    public void StagedOrderingsForKey_SelectsTheOrderingColumnBoundByTheKey()
    {
        Assert.Equal(
            "SELECT [__DS_ChangeOrdering]\nFROM #staging\nWHERE [Id] = @key0\nORDER BY [__DS_ChangeOrdering];",
            HistorizedStatement.BuildStagedOrderingsForKey(BracketDialect.Instance, "#staging", ["Id"]));
    }

    [Fact]
    public void StagedOrderingsForKey_ACompositeKey_BindsOneParameterPerColumn()
    {
        var sql = HistorizedStatement.BuildStagedOrderingsForKey(BracketDialect.Instance, "#staging", ["Region", "Id"]);

        Assert.Contains("WHERE [Region] = @key0 AND [Id] = @key1", sql);
    }

    // ---- BuildCloseChanged: the bulk statement's duplicate-key exclusion --------------------------

    [Fact]
    public void CloseChanged_WithAStagingFilter_AddsItToTheExistsClause()
    {
        var sql = HistorizedStatement.BuildCloseChanged(
            BracketDialect.Instance, "[dbo].[Hist]", "#staging", ["Id"], ["Name"],
            stagingFilter: "(SELECT COUNT(*) FROM #staging AS dup WHERE dup.[Id] = s.[Id]) = 1");

        Assert.Contains(
            "AND (SELECT COUNT(*) FROM #staging AS dup WHERE dup.[Id] = s.[Id]) = 1", sql);
        // Still the pass-wide @now: a staging filter alone (no validToExpression) does not change what
        // closes the version, only which staged rows are allowed to.
        Assert.Contains("[DS_ValidTo] = @now", sql);
    }

    /// <summary>No filter at all — the default — is the exact statement every pairing but CDC still
    /// gets, unchanged since before this phase.</summary>
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
    /// staged column from.
    /// </summary>
    [Fact]
    public void CloseChanged_WithAValidToExpression_ReadsItFromACorrelatedSubquery()
    {
        var sql = HistorizedStatement.BuildCloseChanged(
            BracketDialect.Instance, "[dbo].[Hist]", "#staging", ["Id"], ["Name"],
            validToExpression: "s.[__DS_ChangedAtUtc]",
            stagingFilter: "s.[__DS_ChangeOrdering] = @ordering");

        Assert.Contains(
            "SET [DS_ValidTo] = (SELECT s.[__DS_ChangedAtUtc] FROM #staging AS s WHERE s.[Id] = [dbo].[Hist].[Id]", sql);
        Assert.Contains("AND s.[__DS_ChangeOrdering] = @ordering)", sql);
        Assert.DoesNotContain("@now", sql);
    }

    // ---- BuildOpenVersions: the single-row-scoped surrogate key and ValidFrom ---------------------

    [Fact]
    public void OpenVersions_WithAStagingFilter_AddsItRightAfterTheDeleteCheck()
    {
        var sql = HistorizedStatement.BuildOpenVersions(
            BracketDialect.Instance, "[dbo].[Hist]", "#staging", ["Id"], Mappings,
            stagingFilter: "s.[__DS_ChangeOrdering] = @ordering");

        Assert.Contains("WHERE s.[__Operation] <> 'D' AND s.[__DS_ChangeOrdering] = @ordering", sql);
    }

    [Fact]
    public void OpenVersions_WithNoOptionalArguments_IsByteForByteUnchanged()
    {
        Assert.Equal(
            HistorizedStatement.BuildOpenVersions(BracketDialect.Instance, "[dbo].[Hist]", "#staging", ["Id"], Mappings),
            HistorizedStatement.BuildOpenVersions(
                BracketDialect.Instance, "[dbo].[Hist]", "#staging", ["Id"], Mappings,
                versionKeyPrefixExpression: null, validFromExpression: null, stagingFilter: null));
    }

    /// <summary>
    /// The actual collision fix: a duplicate key's surrogate is built from the staged row's own
    /// <c>OrderingColumn</c> — unique per row by construction — rather than the pass-wide prefix every
    /// other key still uses. Two versions of one key opened in the same pass can no longer compute the
    /// same value.
    /// </summary>
    [Fact]
    public void OpenVersions_WithAVersionKeyPrefixExpression_UsesItInsteadOfThePassWidePrefix()
    {
        // BracketDialect (the generic-layer test double) does not override Concat, so it renders with
        // the ANSI " || " operator here — the same reason the existing composite-natural-key test
        // below sees " || " rather than the SQL Server "+" a real MsSqlDialect would use.
        var sql = HistorizedStatement.BuildOpenVersions(
            BracketDialect.Instance, "[dbo].[Hist]", "#staging", ["Id"], Mappings,
            versionKeyPrefixExpression: "s.[__DS_ChangeOrdering] + '|'",
            stagingFilter: "s.[__DS_ChangeOrdering] = @ordering");

        Assert.Contains("s.[__DS_ChangeOrdering] + '|' || CAST(s.[Id] AS VARCHAR(4000))", sql);
        Assert.DoesNotContain("@versionKeyPrefix", sql);
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

    /// <summary>The real SQL Server dialect concatenates with <c>+</c>, not the generic test double's
    /// ANSI <c>||</c> — confirmed here against the actual dialect <c>Scd2Writer</c> runs with on MsSql,
    /// so the "+ '|' + CAST(...)" shape the phase doc describes is pinned somewhere, not just implied
    /// by the portable tests above.</summary>
    [Fact]
    public void OpenVersions_AgainstTheRealMsSqlDialect_ConcatenatesWithPlus()
    {
        var dialect = DbDataSync.Core.Sql.MsSqlDialect.Instance;
        var sql = HistorizedStatement.BuildOpenVersions(
            dialect, "[dbo].[Hist]", "#staging", ["Id"], Mappings,
            versionKeyPrefixExpression: $"s.[__DS_ChangeOrdering] + '|'",
            validFromExpression: "s.[__DS_ChangedAtUtc]",
            stagingFilter: "s.[__DS_ChangeOrdering] = @ordering");

        Assert.Contains("s.[__DS_ChangeOrdering] + '|' + CAST(s.[Id] AS VARCHAR(4000))", sql);
    }

    /// <summary>The full single-row-scoped shape together — what <c>Scd2Writer</c> actually builds for
    /// each staged row of a duplicate key.</summary>
    [Fact]
    public void OpenVersions_TheFullSingleRowScopedShape()
    {
        var sql = HistorizedStatement.BuildOpenVersions(
            BracketDialect.Instance, "[dbo].[Hist]", "#staging", ["Id"], Mappings,
            versionKeyPrefixExpression: "s.[__DS_ChangeOrdering] + '|'",
            validFromExpression: "s.[__DS_ChangedAtUtc]",
            stagingFilter: "s.[__DS_ChangeOrdering] = @ordering");

        Assert.Equal(
            """
            INSERT INTO [dbo].[Hist] ([DS_VersionKey], [Id], [Name], [DS_ValidFrom], [DS_IsCurrent])
            SELECT s.[__DS_ChangeOrdering] + '|' || CAST(s.[Id] AS VARCHAR(4000)), s.[Id], s.[Name], s.[__DS_ChangedAtUtc], TRUE
            FROM #staging AS s
            WHERE s.[__Operation] <> 'D' AND s.[__DS_ChangeOrdering] = @ordering
              AND NOT EXISTS (
                SELECT 1 FROM [dbo].[Hist] AS t
                WHERE t.[Id] = s.[Id] AND t.[DS_IsCurrent] = TRUE
              );
            """,
            sql);
    }
}
