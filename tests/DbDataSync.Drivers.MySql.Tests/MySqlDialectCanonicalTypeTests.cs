using DbDataSync.Drivers.Abstractions;
using DbDataSync.Core.Sql;

namespace DbDataSync.Drivers.MySql.Tests;

/// <summary>Pins the type-mapping table from phase 25 §2 for MySQL/MariaDB's half: translating a native
/// type into canonical form, and rendering a canonical type another engine produced.</summary>
public sealed class MySqlDialectCanonicalTypeTests
{
    private static readonly MySqlDialect Dialect = MySqlDialect.Instance;

    [Theory]
    [InlineData("smallint", CanonicalTypeKind.Int16)]
    [InlineData("int", CanonicalTypeKind.Int32)]
    [InlineData("bigint", CanonicalTypeKind.Int64)]
    [InlineData("json", CanonicalTypeKind.Json)]
    public void SimpleTypes_TranslateToTheirCanonicalKind(string nativeType, CanonicalTypeKind expected) =>
        Assert.Equal(expected, Dialect.ToCanonicalType(nativeType).Kind);

    [Fact]
    public void TinyintWithDisplayWidthOne_IsBoolean()
    {
        var canonical = Dialect.ToCanonicalType("tinyint(1)");
        Assert.Equal(CanonicalTypeKind.Boolean, canonical.Kind);
    }

    [Fact]
    public void BareTinyint_IsInt8_NotBoolean()
    {
        var canonical = Dialect.ToCanonicalType("tinyint");
        Assert.Equal(CanonicalTypeKind.Int8, canonical.Kind);
    }

    [Theory]
    [InlineData("enum")]
    [InlineData("set")]
    [InlineData("bit")]
    [InlineData("geometry")]
    public void EnumSetBitAndSpatialTypes_AreUnmappable(string nativeType) =>
        Assert.Equal(CanonicalTypeKind.Unmappable, Dialect.ToCanonicalType(nativeType).Kind);

    [Fact]
    public void Varchar_IsAlwaysUnicode()
    {
        var canonical = Dialect.ToCanonicalType("varchar(50)");
        Assert.Equal(CanonicalTypeKind.String, canonical.Kind);
        Assert.Equal(50, canonical.Length);
        Assert.True(canonical.IsUnicode);
    }

    [Fact]
    public void Text_HasNoLength_AndIsMax()
    {
        var canonical = Dialect.ToCanonicalType("text");
        Assert.Equal(CanonicalTypeKind.String, canonical.Kind);
        Assert.True(canonical.IsMax);
    }

    [Fact]
    public void Blob_IsBinaryMax()
    {
        var canonical = Dialect.ToCanonicalType("blob");
        Assert.Equal(CanonicalTypeKind.Binary, canonical.Kind);
        Assert.True(canonical.IsMax);
    }

    [Fact]
    public void Timestamp_IsTimestampTz_WithASourceNote()
    {
        var canonical = Dialect.ToCanonicalType("timestamp");
        Assert.Equal(CanonicalTypeKind.TimestampTz, canonical.Kind);
        Assert.NotNull(canonical.SourceNote);
    }

    [Fact]
    public void Datetime_IsPlainTimestamp_WithNoSourceNote()
    {
        var canonical = Dialect.ToCanonicalType("datetime");
        Assert.Equal(CanonicalTypeKind.Timestamp, canonical.Kind);
        Assert.Null(canonical.SourceNote);
    }

    // --- Reverse direction: rendering canonical types another engine originated. ---

    private static CanonicalType Simple(CanonicalTypeKind kind) => new(kind, null, null, null, false, false);

    [Fact]
    public void RenderingBoolean_ProducesTinyint1_Faithfully()
    {
        var rendered = Dialect.RenderColumnType(Simple(CanonicalTypeKind.Boolean));
        Assert.Equal("tinyint(1)", rendered.Sql);
        Assert.Null(rendered.Fidelity);
    }

    [Fact]
    public void RenderingDecimal18_2_ProducesDecimal18_2_Faithfully()
    {
        var canonical = new CanonicalType(CanonicalTypeKind.Decimal, null, 18, 2, false, false);
        var rendered = Dialect.RenderColumnType(canonical);
        Assert.Equal("decimal(18,2)", rendered.Sql);
        Assert.Null(rendered.Fidelity);
    }

    [Fact]
    public void RenderingNvarchar50_ProducesVarchar50_Faithfully()
    {
        var canonical = new CanonicalType(CanonicalTypeKind.String, 50, null, null, true, false);
        var rendered = Dialect.RenderColumnType(canonical);
        Assert.Equal("varchar(50)", rendered.Sql);
        Assert.Null(rendered.Fidelity);
    }

    [Fact]
    public void RenderingNvarcharMax_ProducesLongtext_WithANoLengthBoundNote()
    {
        var canonical = new CanonicalType(CanonicalTypeKind.String, null, null, null, true, true);
        var rendered = Dialect.RenderColumnType(canonical);
        Assert.Equal("longtext", rendered.Sql);
        Assert.Contains("length bound", rendered.Fidelity);
    }

    [Fact]
    public void RenderingVarcharOver65535_FallsBackToLongtext_WithAFidelityNote()
    {
        var canonical = new CanonicalType(CanonicalTypeKind.String, 100_000, null, null, true, false);
        var rendered = Dialect.RenderColumnType(canonical);
        Assert.Equal("longtext", rendered.Sql);
        Assert.NotNull(rendered.Fidelity);
    }

    [Fact]
    public void RenderingDatetime2Scale7_LosesOneDigitOfPrecision()
    {
        var canonical = new CanonicalType(CanonicalTypeKind.Timestamp, null, null, 7, false, false);
        var rendered = Dialect.RenderColumnType(canonical);
        Assert.Equal("datetime(6)", rendered.Sql);
        Assert.Contains("precision", rendered.Fidelity);
    }

    [Fact]
    public void RenderingDatetimeoffset_ProducesTimestamp_WithAFidelityNote()
    {
        var rendered = Dialect.RenderColumnType(Simple(CanonicalTypeKind.TimestampTz));
        Assert.Equal("timestamp(6)", rendered.Sql);
        Assert.NotNull(rendered.Fidelity);
    }

    [Fact]
    public void RenderingUniqueidentifier_ProducesChar36_WithAFidelityNote()
    {
        var rendered = Dialect.RenderColumnType(Simple(CanonicalTypeKind.Guid));
        Assert.Equal("char(36)", rendered.Sql);
        Assert.NotNull(rendered.Fidelity);
    }

    [Fact]
    public void RenderingVarbinaryMax_ProducesLongblob()
    {
        var canonical = new CanonicalType(CanonicalTypeKind.Binary, null, null, null, false, true);
        var rendered = Dialect.RenderColumnType(canonical);
        Assert.Equal("longblob", rendered.Sql);
    }

    [Fact]
    public void RenderingUnmappable_Throws() =>
        Assert.Throws<InvalidOperationException>(() => Dialect.RenderColumnType(Simple(CanonicalTypeKind.Unmappable)));
}
