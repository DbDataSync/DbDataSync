using DbDataSync.Core.Config;

namespace DbDataSync.Core.Tests;

/// <summary>
/// Phase 124's save-time check: <c>KeyReconcile</c> and <c>KeyReconcileDelete</c> must always be
/// paired, and a <c>KeyReconcile</c> reader needs a cached, fully-mapped source primary key — a
/// keyless or partially-mapped source would leave the anti-join with no column to key on.
/// </summary>
public sealed class KeyReconcilePairingValidationTests
{
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

        ConfigValidation.ValidateKeyReconcilePairing("KeyReconcile", "KeyReconcileDelete", mapping);
    }

    [Theory]
    [InlineData("KeyReconcile", "DeleteInsert")]
    [InlineData("BatchReload", "KeyReconcileDelete")]
    public void MismatchedPairing_IsRejected(string readerKind, string writerKind)
    {
        var mapping = Mapping(
            sourceColumns: [new CachedColumn("Id", "int", false, true, false)],
            columnMappings: [Map("Id", "Id")]);

        var ex = Assert.Throws<ConfigValidationException>(
            () => ConfigValidation.ValidateKeyReconcilePairing(readerKind, writerKind, mapping));

        Assert.Contains("KeyReconcile", ex.Message);
        Assert.Contains("KeyReconcileDelete", ex.Message);
    }

    [Fact]
    public void NeitherKindInvolved_IsNotThisChecksBusiness() =>
        ConfigValidation.ValidateKeyReconcilePairing("Watermark", "DeleteInsert", Mapping());

    [Fact]
    public void KeyReconcile_WithNoCachedSourceColumnsAtAll_IsRejected()
    {
        var mapping = Mapping(sourceColumns: [], columnMappings: [Map("Id", "Id")]);

        var ex = Assert.Throws<ConfigValidationException>(
            () => ConfigValidation.ValidateKeyReconcilePairing("KeyReconcile", "KeyReconcileDelete", mapping));

        Assert.Contains("Refresh metadata", ex.Message);
    }

    [Fact]
    public void KeyReconcile_WithACachedButKeylessSource_IsRejected()
    {
        var mapping = Mapping(
            sourceColumns: [new CachedColumn("Id", "int", false, false, false)],
            columnMappings: [Map("Id", "Id")]);

        var ex = Assert.Throws<ConfigValidationException>(
            () => ConfigValidation.ValidateKeyReconcilePairing("KeyReconcile", "KeyReconcileDelete", mapping));

        Assert.Contains("no primary key", ex.Message);
    }

    [Fact]
    public void KeyReconcile_WithAnUnmappedKeyColumn_IsRejected()
    {
        var mapping = Mapping(
            sourceColumns: [new CachedColumn("Id", "int", false, true, false), new CachedColumn("Region", "nvarchar", false, true, false)],
            columnMappings: [Map("Id", "Id")]); // Region is a key but never mapped

        var ex = Assert.Throws<ConfigValidationException>(
            () => ConfigValidation.ValidateKeyReconcilePairing("KeyReconcile", "KeyReconcileDelete", mapping));

        Assert.Contains("Region", ex.Message);
    }
}
