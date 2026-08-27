using DataSync.Drivers.Abstractions;

namespace DataSync.Drivers.MsSql.Tests;

/// <summary>Pins the type-mapping table from phase 25 §2 for SQL Server's half of it: translating a
/// native type into canonical form (the forward direction of every pair), and rendering a canonical
/// type SQL Server did not originate (the reverse direction — what a Postgres column becomes here).</summary>
public sealed class MsSqlDialectCanonicalTypeTests
{
    private static readonly MsSqlDialect Dialect = MsSqlDialect.Instance;

    [Theory]
    [InlineData("bit", CanonicalTypeKind.Boolean)]
    [InlineData("tinyint", CanonicalTypeKind.Int8)]
    [InlineData("int", CanonicalTypeKind.Int32)]
    [InlineData("bigint", CanonicalTypeKind.Int64)]
    [InlineData("uniqueidentifier", CanonicalTypeKind.Guid)]
    [InlineData("xml", CanonicalTypeKind.Xml)]
    public void SimpleTypes_TranslateToTheirCanonicalKind(string nativeType, CanonicalTypeKind expected) =>
        Assert.Equal(expected, Dialect.ToCanonicalType(nativeType).Kind);

    [Theory]
    [InlineData("sql_variant")]
    [InlineData("hierarchyid")]
    [InlineData("geography")]
    public void TypesWithNoCanonicalEquivalent_AreUnmappable(string nativeType) =>
        Assert.Equal(CanonicalTypeKind.Unmappable, Dialect.ToCanonicalType(nativeType).Kind);

    [Fact]
    public void DecimalKeepsItsPrecisionAndScale()
    {
        var canonical = Dialect.ToCanonicalType("decimal(18,2)");
        Assert.Equal(CanonicalTypeKind.Decimal, canonical.Kind);
        Assert.Equal(18, canonical.Precision);
        Assert.Equal(2, canonical.Scale);
        Assert.Null(canonical.SourceNote);
    }

    [Fact]
    public void Money_CollapsesToDecimal19_4_AndCarriesASourceNoteAboutLostCurrencySemantics()
    {
        var canonical = Dialect.ToCanonicalType("money");
        Assert.Equal(CanonicalTypeKind.Decimal, canonical.Kind);
        Assert.Equal(19, canonical.Precision);
        Assert.Equal(4, canonical.Scale);
        Assert.NotNull(canonical.SourceNote);
    }

    [Fact]
    public void NvarcharKeepsItsLengthAndIsUnicode()
    {
        var canonical = Dialect.ToCanonicalType("nvarchar(50)");
        Assert.Equal(CanonicalTypeKind.String, canonical.Kind);
        Assert.Equal(50, canonical.Length);
        Assert.True(canonical.IsUnicode);
        Assert.False(canonical.IsMax);
    }

    [Fact]
    public void NvarcharMax_HasNoLength_AndIsMax()
    {
        var canonical = Dialect.ToCanonicalType("nvarchar(max)");
        Assert.Equal(CanonicalTypeKind.String, canonical.Kind);
        Assert.Null(canonical.Length);
        Assert.True(canonical.IsMax);
    }

    [Fact]
    public void Datetime2_KeepsItsScale()
    {
        var canonical = Dialect.ToCanonicalType("datetime2(7)");
        Assert.Equal(CanonicalTypeKind.Timestamp, canonical.Kind);
        Assert.Equal(7, canonical.Scale);
    }

    [Fact]
    public void Datetime_IsTimestampWithScale3()
    {
        var canonical = Dialect.ToCanonicalType("datetime");
        Assert.Equal(CanonicalTypeKind.Timestamp, canonical.Kind);
        Assert.Equal(3, canonical.Scale);
    }

    [Fact]
    public void Datetimeoffset_IsTimestampTz()
    {
        var canonical = Dialect.ToCanonicalType("datetimeoffset(7)");
        Assert.Equal(CanonicalTypeKind.TimestampTz, canonical.Kind);
    }

    [Fact]
    public void VarbinaryMax_IsBinaryMax()
    {
        var canonical = Dialect.ToCanonicalType("varbinary(max)");
        Assert.Equal(CanonicalTypeKind.Binary, canonical.Kind);
        Assert.True(canonical.IsMax);
    }

    // --- Reverse direction: rendering a canonical type this dialect did not originate. ---

    private static CanonicalType Simple(CanonicalTypeKind kind) => new(kind, null, null, null, false, false);

    [Fact]
    public void RenderingTextFromAnotherEngine_ProducesNvarcharMax_Faithfully()
    {
        var canonical = new CanonicalType(CanonicalTypeKind.String, null, null, null, true, true);
        var rendered = Dialect.RenderColumnType(canonical);
        Assert.Equal("nvarchar(max)", rendered.Sql);
        Assert.Null(rendered.Fidelity);
    }

    [Fact]
    public void RenderingBoolean_ProducesBit()
    {
        var rendered = Dialect.RenderColumnType(Simple(CanonicalTypeKind.Boolean));
        Assert.Equal("bit", rendered.Sql);
        Assert.Null(rendered.Fidelity);
    }

    [Fact]
    public void RenderingGuid_ProducesUniqueidentifier()
    {
        var rendered = Dialect.RenderColumnType(Simple(CanonicalTypeKind.Guid));
        Assert.Equal("uniqueidentifier", rendered.Sql);
        Assert.Null(rendered.Fidelity);
    }

    [Fact]
    public void RenderingJson_ProducesNvarcharMax_WithAFidelityNote()
    {
        var rendered = Dialect.RenderColumnType(Simple(CanonicalTypeKind.Json));
        Assert.Equal("nvarchar(max)", rendered.Sql);
        Assert.NotNull(rendered.Fidelity);
    }

    [Fact]
    public void RenderingVarbinaryMax_IsFaithful()
    {
        var canonical = new CanonicalType(CanonicalTypeKind.Binary, null, null, null, false, true);
        var rendered = Dialect.RenderColumnType(canonical);
        Assert.Equal("varbinary(max)", rendered.Sql);
        Assert.Null(rendered.Fidelity);
    }

    [Fact]
    public void RenderingUnmappable_Throws() =>
        Assert.Throws<InvalidOperationException>(() => Dialect.RenderColumnType(Simple(CanonicalTypeKind.Unmappable)));

    [Fact]
    public void ASourceNoteFromTheOriginatingEngine_SurvivesIntoTheRenderedFidelity()
    {
        var canonical = new CanonicalType(CanonicalTypeKind.Decimal, null, 19, 4, false, false, "carries currency semantics");
        var rendered = Dialect.RenderColumnType(canonical);
        Assert.Contains("currency semantics", rendered.Fidelity);
    }
}
