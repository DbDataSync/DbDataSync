using DbDataSync.Core.Config;

namespace DbDataSync.Core.Tests;

/// <summary>
/// Phase 190S's two shape-level checks on a mapping's query-shaped sources: exactly one of
/// <see cref="TableSpec.Table"/>/<see cref="SourceTableSpec.Query"/> per source (and every target
/// always names a table), plus the reader-support rules a query-shaped source adds on top. See
/// <see cref="ConfigValidation.ValidateSources"/>/<see cref="ConfigValidation.ValidateQuerySourceReader"/>.
/// </summary>
public sealed class QuerySourceValidationTests
{
    private static TableMappingConfig Mapping(SourceTableSpec source, TableSpec? target = null) => new()
    {
        Name = "orders",
        Sources = [source],
        Targets = [target ?? new TableSpec { ConnectionName = "t", Database = "d", Schema = "dbo", Table = "Orders" }],
    };

    private static SourceTableSpec TableSource(string table = "Orders") =>
        new() { ConnectionName = "s", Database = "d", Schema = "dbo", Table = table };

    private static SourceTableSpec QuerySource(string query = "SELECT * FROM Orders", bool allowSubquery = true) =>
        new() { ConnectionName = "s", Database = "d", Query = query, AllowSubquery = allowSubquery };

    [Fact]
    public void ATableShapedSource_IsFine() =>
        ConfigValidation.ValidateSources(Mapping(TableSource()));

    [Fact]
    public void AQueryShapedSource_IsFine() =>
        ConfigValidation.ValidateSources(Mapping(QuerySource()));

    [Fact]
    public void ASourceNamingNeitherTableNorQuery_IsRejected()
    {
        var problem = Assert.Throws<ConfigValidationException>(
            () => ConfigValidation.ValidateSources(Mapping(new SourceTableSpec { ConnectionName = "s", Database = "d" })));

        Assert.Contains("exactly one of a table or a query", problem.Message);
        Assert.Contains("neither", problem.Message);
    }

    [Fact]
    public void ASourceNamingBothTableAndQuery_IsRejected()
    {
        var source = TableSource();
        source.Query = "SELECT * FROM Orders";

        var problem = Assert.Throws<ConfigValidationException>(() => ConfigValidation.ValidateSources(Mapping(source)));

        Assert.Contains("exactly one of a table or a query", problem.Message);
        Assert.Contains("both", problem.Message);
    }

    [Fact]
    public void ATargetNamingNoTable_IsRejected()
    {
        var problem = Assert.Throws<ConfigValidationException>(() => ConfigValidation.ValidateSources(
            Mapping(TableSource(), target: new TableSpec { ConnectionName = "t", Database = "d" })));

        Assert.Contains("names no table", problem.Message);
    }

    [Fact]
    public void NoQueryShapedSource_ValidatesTheReaderWithoutLookingAtItAtAll() =>
        // A table-shaped mapping never has to answer for a reader Kind this rule doesn't apply to.
        ConfigValidation.ValidateQuerySourceReader(Mapping(TableSource()), readerKind: "MsSqlChangeTracking");

    [Fact]
    public void AQueryShapedSource_WithABatchReloadReader_IsFine() =>
        ConfigValidation.ValidateQuerySourceReader(Mapping(QuerySource()), readerKind: "BatchReload");

    [Fact]
    public void AQueryShapedSource_WithAWatermarkReader_AndSubqueriesAllowed_IsFine() =>
        ConfigValidation.ValidateQuerySourceReader(Mapping(QuerySource(allowSubquery: true)), readerKind: "Watermark");

    [Fact]
    public void AQueryShapedSource_WithAWatermarkReader_AndSubqueriesDisallowed_IsRejected()
    {
        var problem = Assert.Throws<ConfigValidationException>(() => ConfigValidation.ValidateQuerySourceReader(
            Mapping(QuerySource(allowSubquery: false)), readerKind: "Watermark"));

        Assert.Contains("Watermark", problem.Message);
        Assert.Contains("disallows subqueries", problem.Message);
    }

    [Fact]
    public void AQueryShapedSource_WithAReloadReader_AndSubqueriesDisallowed_IsStillFine() =>
        // Soft/silent unavailability, not a save-time rejection: segmenting/relationships/transforms
        // simply won't apply for this source, but the plain query still runs.
        ConfigValidation.ValidateQuerySourceReader(Mapping(QuerySource(allowSubquery: false)), readerKind: "BatchReload");

    [Fact]
    public void AQueryShapedSource_WithAnUnsupportedReader_IsRejected()
    {
        var problem = Assert.Throws<ConfigValidationException>(() => ConfigValidation.ValidateQuerySourceReader(
            Mapping(QuerySource()), readerKind: "MsSqlChangeTracking"));

        Assert.Contains("does not support one", problem.Message);
        Assert.Contains("MsSqlChangeTracking", problem.Message);
    }

    [Fact]
    public void AQueryShapedSource_WithTheMsSqlBatchReloadKind_IsFine() =>
        // "MsSqlBatchReload" resolves to the same reader as "BatchReload" (phase 191S) — both accepted.
        ConfigValidation.ValidateQuerySourceReader(Mapping(QuerySource()), readerKind: "MsSqlBatchReload");
}
