using DbDataSync.Core.Config;

namespace DbDataSync.Core.Tests;

/// <summary>
/// Phase 124's save-time check: <c>KeyReconcile</c> and <c>KeyReconcileDelete</c> must always be
/// paired, and a <c>KeyReconcile</c> reader needs a cached, fully-mapped source primary key — a
/// keyless or partially-mapped source would leave the anti-join with no column to key on. Phase 129
/// widens the pairing to also accept <c>KeyReconcileScd2Close</c>, with two more checks that apply only
/// to that ending: the mapping's own primary writer must be <c>Scd2</c>, and a stated natural key must
/// name exactly the source's primary key (translated through the column mappings) — the only columns
/// <c>KeyReconcile</c> actually stages.
/// </summary>
public sealed class KeyReconcilePairingValidationTests
{
    private static readonly IReadOnlyDictionary<string, string> NoOptions = new Dictionary<string, string>();

    private static TableMappingConfig Mapping(
        IReadOnlyList<CachedColumn>? sourceColumns = null, IReadOnlyList<ColumnMapping>? columnMappings = null) => new()
    {
        Name = "orders",
        Sources = [new SourceTableSpec { ConnectionName = "s", Database = "d", Schema = "dbo", Table = "Orders" }],
        Targets = [new TableSpec { ConnectionName = "t", Database = "d", Schema = "dbo", Table = "Orders" }],
        SourceColumns = sourceColumns is null ? [] : [.. sourceColumns],
        ColumnMappings = columnMappings is null ? [] : [.. columnMappings],
    };

    private static ColumnMapping Map(string source, string target) => new() { SourceColumn = source, TargetColumn = target };

    [Fact]
    public void KeyReconcile_PairedWithKeyReconcileDelete_AndAFullyMappedKey_Passes()
    {
        var mapping = Mapping(
            sourceColumns: [new CachedColumn("Id", "int", false, true, false)],
            columnMappings: [Map("Id", "Id")]);

        ConfigValidation.ValidateKeyReconcilePairing("KeyReconcile", "KeyReconcileDelete", mapping, "MsSqlMerge", NoOptions);
    }

    [Theory]
    [InlineData("KeyReconcile", "DeleteInsert")]
    [InlineData("BatchReload", "KeyReconcileDelete")]
    [InlineData("BatchReload", "KeyReconcileScd2Close")]
    public void MismatchedPairing_IsRejected(string readerKind, string writerKind)
    {
        var mapping = Mapping(
            sourceColumns: [new CachedColumn("Id", "int", false, true, false)],
            columnMappings: [Map("Id", "Id")]);

        var ex = Assert.Throws<ConfigValidationException>(
            () => ConfigValidation.ValidateKeyReconcilePairing(readerKind, writerKind, mapping, "MsSqlMerge", NoOptions));

        Assert.Contains("KeyReconcile", ex.Message);
        Assert.Contains("KeyReconcileDelete", ex.Message);
    }

    [Fact]
    public void NeitherKindInvolved_IsNotThisChecksBusiness() =>
        ConfigValidation.ValidateKeyReconcilePairing("Watermark", "DeleteInsert", Mapping(), "MsSqlMerge", NoOptions);

    [Fact]
    public void KeyReconcile_WithNoCachedSourceColumnsAtAll_IsRejected()
    {
        var mapping = Mapping(sourceColumns: [], columnMappings: [Map("Id", "Id")]);

        var ex = Assert.Throws<ConfigValidationException>(
            () => ConfigValidation.ValidateKeyReconcilePairing("KeyReconcile", "KeyReconcileDelete", mapping, "MsSqlMerge", NoOptions));

        Assert.Contains("Refresh metadata", ex.Message);
    }

    [Fact]
    public void KeyReconcile_WithACachedButKeylessSource_IsRejected()
    {
        var mapping = Mapping(
            sourceColumns: [new CachedColumn("Id", "int", false, false, false)],
            columnMappings: [Map("Id", "Id")]);

        var ex = Assert.Throws<ConfigValidationException>(
            () => ConfigValidation.ValidateKeyReconcilePairing("KeyReconcile", "KeyReconcileDelete", mapping, "MsSqlMerge", NoOptions));

        Assert.Contains("no primary key", ex.Message);
    }

    [Fact]
    public void KeyReconcile_WithAnUnmappedKeyColumn_IsRejected()
    {
        var mapping = Mapping(
            sourceColumns: [new CachedColumn("Id", "int", false, true, false), new CachedColumn("Region", "nvarchar", false, true, false)],
            columnMappings: [Map("Id", "Id")]); // Region is a key but never mapped

        var ex = Assert.Throws<ConfigValidationException>(
            () => ConfigValidation.ValidateKeyReconcilePairing("KeyReconcile", "KeyReconcileDelete", mapping, "MsSqlMerge", NoOptions));

        Assert.Contains("Region", ex.Message);
    }

    [Fact]
    public void KeyReconcile_PairedWithScd2Close_AndAScd2PrimaryWriter_AndNoStatedKey_Passes()
    {
        var mapping = Mapping(
            sourceColumns: [new CachedColumn("Id", "int", false, true, false)],
            columnMappings: [Map("Id", "Id")]);

        ConfigValidation.ValidateKeyReconcilePairing("KeyReconcile", "KeyReconcileScd2Close", mapping, "Scd2", NoOptions);
    }

    [Fact]
    public void KeyReconcile_PairedWithScd2Close_ButPrimaryWriterIsNotScd2_IsRejected()
    {
        var mapping = Mapping(
            sourceColumns: [new CachedColumn("Id", "int", false, true, false)],
            columnMappings: [Map("Id", "Id")]);

        var ex = Assert.Throws<ConfigValidationException>(() =>
            ConfigValidation.ValidateKeyReconcilePairing("KeyReconcile", "KeyReconcileScd2Close", mapping, "MsSqlMerge", NoOptions));

        Assert.Contains("Scd2", ex.Message);
        Assert.Contains("MsSqlMerge", ex.Message);
    }

    [Fact]
    public void KeyReconcile_PairedWithScd2Close_AndAStatedKeyEqualToTheDerivedOne_Passes()
    {
        var mapping = Mapping(
            sourceColumns: [new CachedColumn("Id", "int", false, true, false)],
            columnMappings: [Map("Id", "TargetId")]);
        var options = new Dictionary<string, string> { ["naturalKey"] = "TargetId" };

        ConfigValidation.ValidateKeyReconcilePairing("KeyReconcile", "KeyReconcileScd2Close", mapping, "Scd2", options);
    }

    [Fact]
    public void KeyReconcile_PairedWithScd2Close_AndAStatedKeyNamingADifferentColumn_IsRejected()
    {
        var mapping = Mapping(
            sourceColumns: [new CachedColumn("Id", "int", false, true, false)],
            columnMappings: [Map("Id", "TargetId")]);
        var options = new Dictionary<string, string> { ["naturalKey"] = "SomeOtherColumn" };

        var ex = Assert.Throws<ConfigValidationException>(() =>
            ConfigValidation.ValidateKeyReconcilePairing("KeyReconcile", "KeyReconcileScd2Close", mapping, "Scd2", options));

        Assert.Contains("SomeOtherColumn", ex.Message);
        Assert.Contains("TargetId", ex.Message);
    }
}
