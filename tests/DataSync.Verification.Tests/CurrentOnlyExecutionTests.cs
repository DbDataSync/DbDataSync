using DataSync.Core.Config;
using DataSync.Drivers.Abstractions;
using DataSync.Drivers.Generic;
using DataSync.Verification;

namespace DataSync.Verification.Tests;

/// <summary>
/// The execution half: the target's statement is narrowed and the source's is not.
/// <para>
/// Filtering the source too would be comparing a subset of it against all of the target, which is the
/// same mistake pointing the other way — and it is the mistake a symmetric implementation makes.
/// </para>
/// </summary>
public sealed class CurrentOnlyExecutionTests
{
    private static readonly List<ColumnMapping> Mappings =
    [
        new() { SourceColumn = "Region", TargetColumn = "Region" },
        new() { SourceColumn = "Amount", TargetColumn = "Amount" },
    ];

    private static VerificationEndpoint Endpoint(string table) =>
        new(null!, new AnsiDialect(), "dbo", table);

    /// <summary>Double-quoted identifiers, like the executor's other tests — the point here is the
    /// predicate, not the quoting.</summary>
    private sealed class AnsiDialect : SqlDialect
    {
        public override string QuoteIdentifier(string identifier) => $"\"{identifier}\"";
        public override string ParameterReference(string name) => $"@{name}";
        public override CanonicalType ToCanonicalType(string nativeType) => throw new NotSupportedException();
        public override RenderedColumnType RenderColumnType(CanonicalType type) => throw new NotSupportedException();
    }

    private static (string Source, string Target) Build(VerificationCheckConfig check) =>
        VerificationExecutor.BuildStatements(
            check, Mappings, Endpoint("Orders"), Endpoint("OrdersHistory"));

    private static VerificationCheckConfig Check(bool currentOnly, string? column = null) => new()
    {
        Name = "rows",
        Kind = VerificationCheckKind.RowCount,
        GroupBy = ["Region"],
        CompareCurrentOnly = currentOnly,
        CurrentColumn = column,
    };

    [Fact]
    public void TheTargetIsNarrowed_AndTheSourceIsNot()
    {
        var (source, target) = Build(Check(true, HistorizedColumns.IsCurrent));

        Assert.Contains("\"DS_IsCurrent\" = TRUE", target);
        Assert.DoesNotContain("DS_IsCurrent", source);
    }

    /// <summary>Additive, so an existing check builds exactly what it built before.</summary>
    [Fact]
    public void WithoutTheOption_NeitherSideIsNarrowed()
    {
        var (source, target) = Build(Check(false));

        Assert.DoesNotContain("WHERE", source);
        Assert.DoesNotContain("WHERE", target);
    }

    /// <summary>A snapshot target's filter is a different shape, because a snapshot has no per-row
    /// flag — every copy is complete and "current" means the newest.</summary>
    [Fact]
    public void ASnapshotTarget_IsNarrowedToItsMostRecentCopy()
    {
        var (_, target) = Build(Check(true, HistorizedColumns.SnapshotAt));

        Assert.Contains("SELECT MAX(\"DS_SnapshotAt\") FROM \"dbo\".\"OrdersHistory\"", target);
    }

    /// <summary>Asking for it without naming a column narrows nothing rather than guessing a column
    /// name and producing SQL against something that may not exist.</summary>
    [Fact]
    public void WithNoColumnNamed_NothingIsNarrowed()
    {
        var (_, target) = Build(Check(true));

        Assert.DoesNotContain("WHERE", target);
    }

    /// <summary>A Sum check is the same shape and gets the same treatment.</summary>
    [Fact]
    public void ASumCheck_IsNarrowedToo()
    {
        var (_, target) = VerificationExecutor.BuildStatements(
            new VerificationCheckConfig
            {
                Name = "totals",
                Kind = VerificationCheckKind.Sum,
                GroupBy = ["Region"],
                Measures = ["Amount"],
                CompareCurrentOnly = true,
                CurrentColumn = HistorizedColumns.IsCurrent,
            },
            Mappings, Endpoint("Orders"), Endpoint("OrdersHistory"));

        Assert.Contains("SUM(\"Amount\")", target);
        Assert.Contains("\"DS_IsCurrent\" = TRUE", target);
    }
}
