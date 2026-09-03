using DbDataSync.Core.Config;
using DbDataSync.Drivers.Generic;

namespace DbDataSync.Drivers.Generic.Tests;

/// <summary>
/// The two statements SCD Type 2 turns on, and the snapshot's one. What "changed" means and which
/// version gets closed are the parts with real defects available in them, and both are assertable
/// without a database.
/// </summary>
public sealed class HistorizedStatementTests
{
    private static readonly List<ColumnMapping> Mappings =
    [
        new() { SourceColumn = "Id", TargetColumn = "Id" },
        new() { SourceColumn = "Name", TargetColumn = "Name" },
    ];

    /// <summary>Every pass appends a complete copy: no join, no predicate, nothing compared. That is
    /// the point of a snapshot rather than an omission.</summary>
    [Fact]
    public void ASnapshot_AppendsEverythingWithOneMarker()
    {
        var sql = HistorizedStatement.BuildSnapshotInsert(
            BracketDialect.Instance, "[dbo].[Hist]", Mappings, "#staging");

        Assert.Contains("INSERT INTO [dbo].[Hist] ([Id], [Name], [DS_SnapshotAt])", sql);
        Assert.Contains("SELECT [Id], [Name], @snapshotAt", sql);
        Assert.DoesNotContain("WHERE", sql);
        Assert.DoesNotContain("JOIN", sql);
    }

    /// <summary>
    /// The bug this is written to avoid: `a <> b` is *unknown* when either side is null, so a value
    /// becoming null — or arriving where there was none — would not register and the version would
    /// never close. A target quietly reporting stale data as current is the worst outcome available
    /// here, and it is silent.
    /// </summary>
    [Fact]
    public void TheChangeComparison_IsNullSafe()
    {
        var sql = HistorizedStatement.BuildCloseChanged(
            BracketDialect.Instance, "[dbo].[Hist]", "#staging", ["Id"], ["Name"]);

        // Through CASE, because `IS NULL` is a predicate rather than a value and comparing two of them
        // directly is a syntax error on SQL Server. See NullSafeDiffers.
        Assert.Contains(
            "CASE WHEN [dbo].[Hist].[Name] IS NULL THEN 1 ELSE 0 END <> CASE WHEN s.[Name] IS NULL THEN 1 ELSE 0 END",
            sql);
        Assert.Contains("[dbo].[Hist].[Name] IS NOT NULL AND s.[Name] IS NOT NULL", sql);
    }

    /// <summary>A delete closes a version with no replacement, which is the whole reason the staging
    /// table carries the operation.</summary>
    [Fact]
    public void ADelete_ClosesAVersion()
    {
        var sql = HistorizedStatement.BuildCloseChanged(
            BracketDialect.Instance, "[dbo].[Hist]", "#staging", ["Id"], ["Name"]);

        Assert.Contains("s.[__Operation] = 'D'", sql);
    }

    /// <summary>Only the open one. Closing a closed version would rewrite history that has already
    /// been recorded as ended.</summary>
    [Fact]
    public void OnlyTheOpenVersion_IsClosed()
    {
        var sql = HistorizedStatement.BuildCloseChanged(
            BracketDialect.Instance, "[dbo].[Hist]", "#staging", ["Id"], ["Name"]);

        Assert.Contains("WHERE [DS_IsCurrent] = TRUE", sql);
        Assert.Contains("[DS_ValidTo] = @now", sql);
    }

    /// <summary>A table whose every mapped column is part of the key has nothing that can change, so
    /// only a delete closes anything — and the predicate says so rather than producing an empty OR.</summary>
    [Fact]
    public void WithNoValueColumns_OnlyADeleteCloses()
    {
        var sql = HistorizedStatement.BuildCloseChanged(
            BracketDialect.Instance, "[dbo].[Hist]", "#staging", ["Id"], []);

        Assert.Contains("1 = 0", sql);
        Assert.Contains("s.[__Operation] = 'D'", sql);
    }

    /// <summary>
    /// "No open version" is what insert means here, which is why closing runs first: afterwards it is
    /// exactly the new keys plus the ones this pass just closed, and the statement needs no notion of
    /// which is which.
    /// </summary>
    [Fact]
    public void AVersionIsOpened_OnlyWhereThereIsNoOpenOne()
    {
        var sql = HistorizedStatement.BuildOpenVersions(
            BracketDialect.Instance, "[dbo].[Hist]", "#staging", ["Id"], Mappings);

        Assert.Contains("NOT EXISTS", sql);
        Assert.Contains("t.[Id] = s.[Id] AND t.[DS_IsCurrent] = TRUE", sql);
        Assert.Contains("s.[__Operation] <> 'D'", sql);
    }

    /// <summary>A deleted key gets no replacement version — that is the difference between "this
    /// record ended" and "this record changed".</summary>
    [Fact]
    public void ADeletedKey_GetsNoNewVersion() =>
        Assert.Contains(
            "s.[__Operation] <> 'D'",
            HistorizedStatement.BuildOpenVersions(
                BracketDialect.Instance, "[dbo].[Hist]", "#staging", ["Id"], Mappings));

    /// <summary>Computed before insert from the natural key and the pass's own prefix, so nothing has
    /// to be read back from the target and ApplyAsync's signature is unchanged.</summary>
    [Fact]
    public void TheSurrogateKey_IsComputedFromTheNaturalKey()
    {
        var sql = HistorizedStatement.BuildOpenVersions(
            BracketDialect.Instance, "[dbo].[Hist]", "#staging", ["Id"], Mappings);

        Assert.Contains("[DS_VersionKey]", sql);
        Assert.Contains("@versionKeyPrefix", sql);
        Assert.Contains("CAST(s.[Id] AS VARCHAR(4000))", sql);
    }

    /// <summary>A composite natural key is several columns in the surrogate and several in the join —
    /// the versioning is by key, and a key is however many columns it is.</summary>
    [Fact]
    public void ACompositeNaturalKey_IsJoinedAndHashedOnEveryPart()
    {
        var sql = HistorizedStatement.BuildOpenVersions(
            BracketDialect.Instance, "[dbo].[Hist]", "#staging", ["Region", "Id"], Mappings);

        Assert.Contains("t.[Region] = s.[Region] AND t.[Id] = s.[Id]", sql);
        Assert.Contains("CAST(s.[Region] AS VARCHAR(4000)) || '|' || CAST(s.[Id] AS VARCHAR(4000))", sql);
    }

    /// <summary>
    /// A boolean literal is `TRUE` on most engines and `1` on SQL Server, whose canonical Boolean is
    /// `bit` — and `= 1` against a Postgres `boolean` is "operator does not exist". Concatenation is
    /// the same problem: `||` everywhere, `+` on SQL Server. Both were found by running the writer
    /// against a real database, not by reading.
    /// </summary>
    [Fact]
    public void BooleansAndConcatenation_ComeFromTheDialect()
    {
        var ansi = HistorizedStatement.BuildCloseChanged(
            ColonDialect.Instance, "\"hist\"", "staging", ["id"], ["name"]);

        Assert.Contains("\"DS_IsCurrent\" = TRUE", ansi);
        Assert.Contains("\"DS_IsCurrent\" = FALSE", ansi);
    }

    /// <summary>Engine-neutral, which is the claim placing these in the generic layer makes.</summary>
    [Fact]
    public void TheSameStatementsAreBuiltForAnotherDialect()
    {
        var sql = HistorizedStatement.BuildSnapshotInsert(
            ColonDialect.Instance, "\"public\".\"hist\"", Mappings, "staging");

        Assert.Contains("\"DS_SnapshotAt\"", sql);
        Assert.Contains(":snapshotAt", sql);
        Assert.DoesNotContain("[", sql);
    }
}
