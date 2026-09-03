using DbDataSync.Core.Config;
using DbDataSync.Drivers.Abstractions;
using DbDataSync.Core.Sql;

namespace DbDataSync.Drivers.Generic.Tests;

public sealed class ProvisioningColumnBuilderTests
{
    private static ColumnMapping Map(string source, string target) => new() { SourceColumn = source, TargetColumn = target };

    [Fact]
    public void TranslatesEveryMappedColumnByItsSourceMetadata()
    {
        var sourceColumns = new[]
        {
            new ColumnMetadata("Id", "int", IsNullable: false, IsPrimaryKey: true, IsIdentity: false),
            new ColumnMetadata("Name", "text", IsNullable: true, IsPrimaryKey: false, IsIdentity: false),
        };
        var mappings = new[] { Map("Id", "OrderId"), Map("Name", "OrderName") };

        var (columns, warnings) = ProvisioningColumnBuilder.Build(RecordingDialect.Instance, sourceColumns, mappings);

        Assert.Equal(2, columns.Count);
        Assert.Equal("OrderId", columns[0].Name);
        Assert.True(columns[0].IsPrimaryKey);
        Assert.False(columns[0].IsNullable);
        Assert.Equal("OrderName", columns[1].Name);
        Assert.True(columns[1].IsNullable);
        Assert.Empty(warnings);
    }

    [Fact]
    public void AnIdentitySourceColumn_IsStillIncluded_ButWarnsThatItIsCreatedPlain()
    {
        var sourceColumns = new[] { new ColumnMetadata("Id", "int", IsNullable: false, IsPrimaryKey: true, IsIdentity: true) };
        var mappings = new[] { Map("Id", "Id") };

        var (columns, warnings) = ProvisioningColumnBuilder.Build(RecordingDialect.Instance, sourceColumns, mappings);

        Assert.Single(columns);
        var warning = Assert.Single(warnings);
        Assert.Contains("Id", warning);
        Assert.Contains("plain", warning);
    }

    [Fact]
    public void AMappedColumnMissingFromSourceMetadata_Throws()
    {
        var mappings = new[] { Map("Ghost", "Ghost") };

        Assert.Throws<InvalidOperationException>(
            () => ProvisioningColumnBuilder.Build(RecordingDialect.Instance, [], mappings));
    }

    /// <summary>Records exactly which native type strings it was asked to translate, so a test can
    /// assert the builder calls <c>ToCanonicalType</c> with the source's own native type rather than
    /// anything else.</summary>
    private sealed class RecordingDialect : SqlDialect
    {
        public static RecordingDialect Instance { get; } = new();
        public override string QuoteIdentifier(string identifier) => identifier;
        public override string ParameterReference(string name) => name;

        public override CanonicalType ToCanonicalType(string nativeType) => nativeType switch
        {
            "int" => new CanonicalType(CanonicalTypeKind.Int32, null, null, null, false, false),
            "text" => new CanonicalType(CanonicalTypeKind.String, null, null, null, true, true),
            _ => new CanonicalType(CanonicalTypeKind.Unmappable, null, null, null, false, false),
        };

        public override RenderedColumnType RenderColumnType(CanonicalType type) => throw new NotSupportedException();
    }
}
