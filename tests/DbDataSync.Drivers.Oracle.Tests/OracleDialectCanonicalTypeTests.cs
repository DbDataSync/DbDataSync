using DbDataSync.Drivers.Abstractions;
using DbDataSync.Core.Sql;

namespace DbDataSync.Drivers.Oracle.Tests;

/// <summary>Pins the type-mapping table from phase 25 §2 for Oracle's half: translating a native type
/// into canonical form, and rendering a canonical type another engine produced.</summary>
public sealed class OracleDialectCanonicalTypeTests
{
    private static readonly OracleDialect Dialect = OracleDialect.Instance;

    [Fact]
    public void BareNumber_IsApproximatedAsDecimal38_10_WithASourceNote()
    {
        var canonical = Dialect.ToCanonicalType("NUMBER");
        Assert.Equal(CanonicalTypeKind.Decimal, canonical.Kind);
        Assert.Equal(38, canonical.Precision);
        Assert.Equal(10, canonical.Scale);
        Assert.NotNull(canonical.SourceNote);
    }

    [Theory]
    [InlineData("NUMBER(4,0)", CanonicalTypeKind.Int16)]
    [InlineData("NUMBER(9,0)", CanonicalTypeKind.Int32)]
    [InlineData("NUMBER(18,0)", CanonicalTypeKind.Int64)]
    [InlineData("NUMBER(19,0)", CanonicalTypeKind.Decimal)]
    public void NumberWithZeroScale_ClassifiesByPrecision(string nativeType, CanonicalTypeKind expected) =>
        Assert.Equal(expected, Dialect.ToCanonicalType(nativeType).Kind);

    [Fact]
    public void NumberWithNonZeroScale_IsDecimal()
    {
        var canonical = Dialect.ToCanonicalType("NUMBER(18,2)");
        Assert.Equal(CanonicalTypeKind.Decimal, canonical.Kind);
        Assert.Equal(18, canonical.Precision);
        Assert.Equal(2, canonical.Scale);
    }

    [Fact]
    public void Date_IsTimestampAtScaleZero_NotDateOnly()
    {
        var canonical = Dialect.ToCanonicalType("DATE");
        Assert.Equal(CanonicalTypeKind.Timestamp, canonical.Kind);
        Assert.Equal(0, canonical.Scale);
    }

    [Fact]
    public void TimestampWithItsOwnEmbeddedPrecision_ParsesCorrectly()
    {
        // Confirmed against a live server: ALL_TAB_COLUMNS.DATA_TYPE for a TIMESTAMP column already
        // includes its precision, unlike every other type family — see OracleCatalog's own doc comment.
        var canonical = Dialect.ToCanonicalType("TIMESTAMP(6)");
        Assert.Equal(CanonicalTypeKind.Timestamp, canonical.Kind);
        Assert.Equal(6, canonical.Scale);
    }

    [Fact]
    public void TimestampWithTimeZone_IsTimestampTz_DespiteCanonicalTypeSpecTruncatingTheSuffix()
    {
        // CanonicalTypeSpec.Parse truncates at the first ')', which would silently drop "WITH TIME
        // ZONE" if this dialect relied on it alone — this is the test for the explicit substring check
        // OracleDialect.ToCanonicalType does before falling through to the shared parser.
        var canonical = Dialect.ToCanonicalType("TIMESTAMP(6) WITH TIME ZONE");
        Assert.Equal(CanonicalTypeKind.TimestampTz, canonical.Kind);
        Assert.Equal(6, canonical.Scale);
    }

    [Fact]
    public void TimestampWithLocalTimeZone_IsAlsoTimestampTz()
    {
        var canonical = Dialect.ToCanonicalType("TIMESTAMP(6) WITH LOCAL TIME ZONE");
        Assert.Equal(CanonicalTypeKind.TimestampTz, canonical.Kind);
    }

    [Fact]
    public void Varchar2_IsNotUnicode_Nvarchar2Is()
    {
        Assert.False(Dialect.ToCanonicalType("VARCHAR2(50)").IsUnicode);
        Assert.True(Dialect.ToCanonicalType("NVARCHAR2(50)").IsUnicode);
    }

    [Fact]
    public void Clob_HasNoLength_AndIsMax()
    {
        var canonical = Dialect.ToCanonicalType("CLOB");
        Assert.Equal(CanonicalTypeKind.String, canonical.Kind);
        Assert.True(canonical.IsMax);
    }

    [Theory]
    [InlineData("ROWID")]
    [InlineData("UROWID")]
    [InlineData("SDO_GEOMETRY")]
    public void RowidAndSpatialTypes_AreUnmappable(string nativeType) =>
        Assert.Equal(CanonicalTypeKind.Unmappable, Dialect.ToCanonicalType(nativeType).Kind);

    // --- Reverse direction: rendering canonical types another engine originated. ---

    private static CanonicalType Simple(CanonicalTypeKind kind) => new(kind, null, null, null, false, false);

    [Fact]
    public void RenderingBoolean_ProducesNumber1_Faithfully()
    {
        var rendered = Dialect.RenderColumnType(Simple(CanonicalTypeKind.Boolean));
        Assert.Equal("number(1)", rendered.Sql);
        Assert.Null(rendered.Fidelity);
    }

    [Fact]
    public void RenderingDecimal18_2_ProducesNumber18_2_Faithfully()
    {
        var canonical = new CanonicalType(CanonicalTypeKind.Decimal, null, 18, 2, false, false);
        var rendered = Dialect.RenderColumnType(canonical);
        Assert.Equal("number(18,2)", rendered.Sql);
        Assert.Null(rendered.Fidelity);
    }

    [Fact]
    public void RenderingNvarchar50_ProducesNvarchar250_Faithfully()
    {
        var canonical = new CanonicalType(CanonicalTypeKind.String, 50, null, null, true, false);
        var rendered = Dialect.RenderColumnType(canonical);
        Assert.Equal("nvarchar2(50)", rendered.Sql);
        Assert.Null(rendered.Fidelity);
    }

    [Fact]
    public void RenderingVarchar50_ProducesVarchar250_NotNvarchar()
    {
        var canonical = new CanonicalType(CanonicalTypeKind.String, 50, null, null, false, false);
        var rendered = Dialect.RenderColumnType(canonical);
        Assert.Equal("varchar2(50)", rendered.Sql);
    }

    [Fact]
    public void RenderingNvarcharMax_ProducesNclob_WithANoLengthBoundNote()
    {
        var canonical = new CanonicalType(CanonicalTypeKind.String, null, null, null, true, true);
        var rendered = Dialect.RenderColumnType(canonical);
        Assert.Equal("nclob", rendered.Sql);
        Assert.Contains("length bound", rendered.Fidelity);
    }

    [Fact]
    public void RenderingRawOver2000Bytes_FallsBackToBlob_WithAFidelityNote()
    {
        var canonical = new CanonicalType(CanonicalTypeKind.Binary, 4000, null, null, false, false);
        var rendered = Dialect.RenderColumnType(canonical);
        Assert.Equal("blob", rendered.Sql);
        Assert.NotNull(rendered.Fidelity);
    }

    [Fact]
    public void RenderingTime_ProducesTimestamp_WithAFidelityNote()
    {
        var rendered = Dialect.RenderColumnType(Simple(CanonicalTypeKind.Time));
        Assert.StartsWith("timestamp(", rendered.Sql);
        Assert.Contains("no native TIME", rendered.Fidelity);
    }

    [Fact]
    public void RenderingTimestampTz_ProducesTimestampWithTimeZone_Faithfully()
    {
        var rendered = Dialect.RenderColumnType(Simple(CanonicalTypeKind.TimestampTz));
        Assert.EndsWith("with time zone", rendered.Sql);
        Assert.Null(rendered.Fidelity);
    }

    [Fact]
    public void RenderingGuid_ProducesVarchar236_WithAFidelityNote()
    {
        var rendered = Dialect.RenderColumnType(Simple(CanonicalTypeKind.Guid));
        Assert.Equal("varchar2(36)", rendered.Sql);
        Assert.NotNull(rendered.Fidelity);
    }

    [Fact]
    public void RenderingJson_ProducesClob_WithAVersionFloorNote()
    {
        var rendered = Dialect.RenderColumnType(Simple(CanonicalTypeKind.Json));
        Assert.Equal("clob", rendered.Sql);
        Assert.Contains("21c", rendered.Fidelity);
    }

    [Fact]
    public void RenderingUnmappable_Throws() =>
        Assert.Throws<InvalidOperationException>(() => Dialect.RenderColumnType(Simple(CanonicalTypeKind.Unmappable)));
}
