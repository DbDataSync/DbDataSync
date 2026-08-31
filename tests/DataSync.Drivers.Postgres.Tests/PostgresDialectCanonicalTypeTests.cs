using DataSync.Drivers.Abstractions;
using DataSync.Core.Sql;

namespace DataSync.Drivers.Postgres.Tests;

/// <summary>Pins the type-mapping table from phase 25 §2 for PostgreSQL's half: translating a native
/// type into canonical form, and rendering a canonical type another engine produced (SQL Server's
/// column types, arriving here as the reverse-direction cases the doc calls out by name).</summary>
public sealed class PostgresDialectCanonicalTypeTests
{
    private static readonly PostgresDialect Dialect = PostgresDialect.Instance;

    [Theory]
    [InlineData("boolean", CanonicalTypeKind.Boolean)]
    [InlineData("integer", CanonicalTypeKind.Int32)]
    [InlineData("bigint", CanonicalTypeKind.Int64)]
    [InlineData("uuid", CanonicalTypeKind.Guid)]
    [InlineData("xml", CanonicalTypeKind.Xml)]
    [InlineData("jsonb", CanonicalTypeKind.Json)]
    public void SimpleTypes_TranslateToTheirCanonicalKind(string nativeType, CanonicalTypeKind expected) =>
        Assert.Equal(expected, Dialect.ToCanonicalType(nativeType).Kind);

    [Theory]
    [InlineData("ARRAY")]
    [InlineData("array")]
    [InlineData("USER-DEFINED")]
    public void ArrayAndUserDefinedTypes_AreUnmappable(string nativeType) =>
        Assert.Equal(CanonicalTypeKind.Unmappable, Dialect.ToCanonicalType(nativeType.ToLowerInvariant()).Kind);

    [Fact]
    public void CharacterVarying_IsAlwaysUnicode()
    {
        var canonical = Dialect.ToCanonicalType("character varying(50)");
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
        Assert.True(canonical.IsUnicode);
    }

    [Fact]
    public void Bytea_IsBinaryMax()
    {
        var canonical = Dialect.ToCanonicalType("bytea");
        Assert.Equal(CanonicalTypeKind.Binary, canonical.Kind);
        Assert.True(canonical.IsMax);
    }

    [Fact]
    public void TimestampWithTimeZone_IsTimestampTz()
    {
        var canonical = Dialect.ToCanonicalType("timestamp with time zone");
        Assert.Equal(CanonicalTypeKind.TimestampTz, canonical.Kind);
    }

    // --- Reverse direction: rendering canonical types SQL Server originated. ---

    private static CanonicalType Simple(CanonicalTypeKind kind) => new(kind, null, null, null, false, false);

    [Fact]
    public void RenderingBoolean_ProducesBoolean_Faithfully()
    {
        var rendered = Dialect.RenderColumnType(Simple(CanonicalTypeKind.Boolean));
        Assert.Equal("boolean", rendered.Sql);
        Assert.Null(rendered.Fidelity);
    }

    [Fact]
    public void RenderingTinyintDerivedInt8_WidensToSmallint_WithAFidelityNote()
    {
        var rendered = Dialect.RenderColumnType(Simple(CanonicalTypeKind.Int8));
        Assert.Equal("smallint", rendered.Sql);
        Assert.NotNull(rendered.Fidelity);
    }

    [Fact]
    public void RenderingDecimal18_2_ProducesNumeric18_2_Faithfully()
    {
        var canonical = new CanonicalType(CanonicalTypeKind.Decimal, null, 18, 2, false, false);
        var rendered = Dialect.RenderColumnType(canonical);
        Assert.Equal("numeric(18,2)", rendered.Sql);
        Assert.Null(rendered.Fidelity);
    }

    [Fact]
    public void RenderingMoneyDerivedDecimal19_4_ProducesNumeric19_4_WithTheSourceNote()
    {
        var canonical = new CanonicalType(CanonicalTypeKind.Decimal, null, 19, 4, false, false, "carries currency semantics");
        var rendered = Dialect.RenderColumnType(canonical);
        Assert.Equal("numeric(19,4)", rendered.Sql);
        Assert.Contains("currency semantics", rendered.Fidelity);
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
    public void RenderingNvarcharMax_ProducesText_WithANoLengthBoundNote()
    {
        var canonical = new CanonicalType(CanonicalTypeKind.String, null, null, null, true, true);
        var rendered = Dialect.RenderColumnType(canonical);
        Assert.Equal("text", rendered.Sql);
        Assert.Contains("length bound", rendered.Fidelity);
    }

    [Fact]
    public void RenderingDatetime2Scale7_LosesOneDigitOfPrecision()
    {
        var canonical = new CanonicalType(CanonicalTypeKind.Timestamp, null, null, 7, false, false);
        var rendered = Dialect.RenderColumnType(canonical);
        Assert.Equal("timestamp(6)", rendered.Sql);
        Assert.Contains("precision", rendered.Fidelity);
    }

    [Fact]
    public void RenderingDatetimeScale3_IsFaithful()
    {
        var canonical = new CanonicalType(CanonicalTypeKind.Timestamp, null, null, 3, false, false);
        var rendered = Dialect.RenderColumnType(canonical);
        Assert.Equal("timestamp(3)", rendered.Sql);
        Assert.Null(rendered.Fidelity);
    }

    [Fact]
    public void RenderingDatetimeoffset_ProducesTimestamptz_Faithfully()
    {
        var rendered = Dialect.RenderColumnType(Simple(CanonicalTypeKind.TimestampTz));
        Assert.Equal("timestamptz", rendered.Sql);
        Assert.Null(rendered.Fidelity);
    }

    [Fact]
    public void RenderingUniqueidentifier_ProducesUuid_Faithfully()
    {
        var rendered = Dialect.RenderColumnType(Simple(CanonicalTypeKind.Guid));
        Assert.Equal("uuid", rendered.Sql);
        Assert.Null(rendered.Fidelity);
    }

    [Fact]
    public void RenderingVarbinaryMax_ProducesBytea_Faithfully()
    {
        var canonical = new CanonicalType(CanonicalTypeKind.Binary, null, null, null, false, true);
        var rendered = Dialect.RenderColumnType(canonical);
        Assert.Equal("bytea", rendered.Sql);
        Assert.Null(rendered.Fidelity);
    }

    [Fact]
    public void RenderingUnmappable_Throws() =>
        Assert.Throws<InvalidOperationException>(() => Dialect.RenderColumnType(Simple(CanonicalTypeKind.Unmappable)));
}
