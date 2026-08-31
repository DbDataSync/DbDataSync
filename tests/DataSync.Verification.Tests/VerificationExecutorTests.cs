using DataSync.Core.Config;
using DataSync.Drivers.Abstractions;
using DataSync.Drivers.Generic;
using DataSync.Scripting.Abstractions;
using DataSync.Verification;
using DataSync.Core.Sql;

namespace DataSync.Verification.Tests;

/// <summary>
/// What each side is asked, for every way a check can be authored. No connections here — this is the
/// statement-building half, and it is a pure function of the check and the mapping.
/// </summary>
public sealed class VerificationExecutorTests
{
    private static readonly List<ColumnMapping> Mappings =
    [
        new() { SourceColumn = "cust_id", TargetColumn = "CustomerId" },
        new() { SourceColumn = "region", TargetColumn = "Region" },
        new() { SourceColumn = "country", TargetColumn = "Country" },
        new() { SourceColumn = "channel", TargetColumn = "Channel" },
        new() { SourceColumn = "segment", TargetColumn = "Segment" },
        new() { SourceColumn = "amt", TargetColumn = "Amount" },
    ];

    private static VerificationEndpoint Side(string schema, string table) =>
        new(null!, new AnsiDialect(), schema, table, new AnsiScriptDialect());

    private static (string Source, string Target) Build(
        VerificationCheckConfig check, IVerificationQueryBuilder? builder = null) =>
        VerificationExecutor.BuildStatements(
            check, Mappings, Side("src", "Orders"), Side("dbo", "Orders"), builder);

    /// <summary>
    /// The plan called for the fixed cap of three grouping columns to be gone. It never existed here —
    /// cardinality is a per-parameter min/max under phase 42's ceiling — and this is the assertion
    /// that keeps it gone.
    /// </summary>
    [Fact]
    public void AGroupedCheck_TakesMoreThanThreeGroupingColumns()
    {
        var check = new VerificationCheckConfig
        {
            Name = "many-groups",
            GroupBy = ["CustomerId", "Region", "Country", "Channel", "Segment"],
        };

        var (source, target) = Build(check);

        foreach (var column in new[] { "cust_id", "region", "country", "channel", "segment" })
            Assert.Contains(column, source);
        foreach (var column in new[] { "CustomerId", "Region", "Country", "Channel", "Segment" })
            Assert.Contains(column, target);
    }

    /// <summary>
    /// One statement, run against both sides — the operator's judgement that the two engines are close
    /// enough here, rather than something inferred for them.
    /// </summary>
    [Fact]
    public void AGenericSqlCheck_RunsTheSameStatementOnBothSides()
    {
        var check = new VerificationCheckConfig
        {
            Name = "generic",
            Kind = VerificationCheckKind.Sql,
            SourceSql = "SELECT COUNT(*) AS n FROM Orders;",
            Measures = ["n"],
        };

        var (source, target) = Build(check);

        Assert.Equal(check.SourceSql, source);
        Assert.Equal(check.SourceSql, target);
    }

    [Fact]
    public void APerDialectSqlCheck_RunsEachSidesOwnStatement()
    {
        var check = new VerificationCheckConfig
        {
            Name = "per-dialect",
            Kind = VerificationCheckKind.Sql,
            SourceSql = "SELECT TOP 1 COUNT(*) AS n FROM src.Orders;",
            TargetSql = "SELECT COUNT(*) AS n FROM dbo.Orders LIMIT 1;",
            Measures = ["n"],
        };

        var (source, target) = Build(check);

        Assert.Equal(check.SourceSql, source);
        Assert.Equal(check.TargetSql, target);
    }

    [Fact]
    public void ASqlCheckWithNoStatement_IsRefused()
    {
        var check = new VerificationCheckConfig { Name = "empty", Kind = VerificationCheckKind.Sql };

        var ex = Assert.Throws<ConfigValidationException>(() => Build(check));
        Assert.Contains("has no SQL", ex.Message);
    }

    /// <summary>
    /// Called once per side, so a generated check can produce genuinely different SQL for two engines
    /// — which is the case a per-dialect hand-written check covers and a generic one does not.
    /// </summary>
    [Fact]
    public void AScriptCheck_IsAskedOncePerSide()
    {
        var check = new VerificationCheckConfig
        {
            Name = "generated", Kind = VerificationCheckKind.Script, ScriptName = "counts",
        };

        var (source, target) = Build(check, new SideAwareBuilder());

        Assert.Equal("SELECT 1 -- Source src.Orders", source);
        Assert.Equal("SELECT 1 -- Target dbo.Orders", target);
    }

    [Fact]
    public void AScriptCheckWhoseScriptIsMissing_IsRefusedNamingIt()
    {
        var check = new VerificationCheckConfig
        {
            Name = "generated", Kind = VerificationCheckKind.Script, ScriptName = "nowhere",
        };

        var ex = Assert.Throws<ConfigValidationException>(() => Build(check));
        Assert.Contains("nowhere", ex.Message);
    }

    /// <summary>A builder that answers differently per side, which is the whole reason it is asked
    /// twice.</summary>
    private sealed class SideAwareBuilder : IVerificationQueryBuilder
    {
        public SourceQuery BuildQuery(VerificationQueryContext c) =>
            SourceQuery.Text($"SELECT 1 -- {c.Side} {c.Table.Schema}.{c.Table.Table}");

        public VerificationQueryShape DescribeResult(VerificationQueryContext c) => new(["Region"], ["n"]);
    }

    private sealed class AnsiDialect : SqlDialect
    {
        public override string QuoteIdentifier(string identifier) => $"\"{identifier}\"";
        public override string ParameterReference(string name) => $"@{name}";
        public override CanonicalType ToCanonicalType(string nativeType) => throw new NotSupportedException();
        public override RenderedColumnType RenderColumnType(CanonicalType type) => throw new NotSupportedException();
    }

    private sealed class AnsiScriptDialect : IScriptDialect
    {
        public string EngineName => "ANSI";
        public string QuoteIdentifier(string identifier) => $"\"{identifier}\"";
        public string ParameterReference(string name) => $"@{name}";
        public CanonicalType ToCanonicalType(string nativeType) => throw new NotSupportedException();
        public RenderedColumnType RenderColumnType(CanonicalType type) => throw new NotSupportedException();
    }
}
