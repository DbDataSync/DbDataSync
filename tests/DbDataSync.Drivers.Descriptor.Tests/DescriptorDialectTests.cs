using DbDataSync.Core.Sql;
using DbDataSync.Drivers.Descriptor;
using Xunit;

namespace DbDataSync.Drivers.Descriptor.Tests;

public sealed class DescriptorDialectTests
{
    private const string MySqlGenericYaml = """
        id: mysql.generic
        displayName: MySQL / MariaDB (generic)
        library: mysql-connector
        dialect:
          quoteIdentifier: backtick
          parameterPrefix: "@"
          rowLimit: limitOffset
          catalog: informationSchema
          supportsChangeDatabase: true
        typeMap:
          tinyint: Int8
          smallint: Int16
          int: Int32
          bigint: Int64
          "decimal(p,s)": { kind: Decimal, precision: p, scale: s }
          double: Double
          "varchar(n)": { kind: String, length: n, unicode: true }
          text: { kind: String, max: true }
          datetime: Timestamp
          date: Date
          json: Json
          blob: { kind: Binary, max: true }
        capabilities:
          readers: [Watermark, BatchReload]
          staging: [StagingTable]
          writers: [DeleteInsert]
        """;

    private static DescriptorDialect Dialect(string yaml = MySqlGenericYaml)
    {
        var descriptor = DriverDescriptorReader.Deserialize(yaml);
        return new DescriptorDialect(descriptor.Dialect, descriptor.TypeMap);
    }

    [Fact]
    public void ParsesTheWholeDescriptor()
    {
        var descriptor = DriverDescriptorReader.Deserialize(MySqlGenericYaml);

        Assert.Equal("mysql.generic", descriptor.Id);
        Assert.Equal("MySQL / MariaDB (generic)", descriptor.DisplayName);
        Assert.Equal("mysql-connector", descriptor.Library);
        Assert.Equal(["Watermark", "BatchReload"], descriptor.Capabilities.Readers);
        Assert.Equal(["StagingTable"], descriptor.Capabilities.Staging);
        Assert.Equal(["DeleteInsert"], descriptor.Capabilities.Writers);
    }

    [Theory]
    [InlineData("int", CanonicalTypeKind.Int32)]
    [InlineData("bigint", CanonicalTypeKind.Int64)]
    [InlineData("json", CanonicalTypeKind.Json)]
    [InlineData("datetime", CanonicalTypeKind.Timestamp)]
    public void PlainScalarEntries_MapDirectlyToTheirKind(string nativeType, CanonicalTypeKind expected)
    {
        var canonical = Dialect().ToCanonicalType(nativeType);

        Assert.Equal(expected, canonical.Kind);
        Assert.Null(canonical.Precision);
        Assert.Null(canonical.Scale);
        Assert.Null(canonical.Length);
    }

    [Fact]
    public void DecimalWithPrecisionAndScale_SubstitutesThePlaceholders()
    {
        var canonical = Dialect().ToCanonicalType("decimal(10,2)");

        Assert.Equal(CanonicalTypeKind.Decimal, canonical.Kind);
        Assert.Equal(10, canonical.Precision);
        Assert.Equal(2, canonical.Scale);
    }

    [Fact]
    public void VarcharWithLength_SubstitutesThePlaceholder_AndCarriesUnicode()
    {
        var canonical = Dialect().ToCanonicalType("varchar(255)");

        Assert.Equal(CanonicalTypeKind.String, canonical.Kind);
        Assert.Equal(255, canonical.Length);
        Assert.True(canonical.IsUnicode);
        Assert.False(canonical.IsMax);
    }

    [Fact]
    public void MaxEntries_CarryNoLength()
    {
        var text = Dialect().ToCanonicalType("text");
        var blob = Dialect().ToCanonicalType("blob");

        Assert.True(text.IsMax);
        Assert.Null(text.Length);
        Assert.Equal(CanonicalTypeKind.Binary, blob.Kind);
        Assert.True(blob.IsMax);
    }

    [Fact]
    public void AnUnlistedNativeType_IsUnmappable()
    {
        var canonical = Dialect().ToCanonicalType("geometry");

        Assert.Equal(CanonicalTypeKind.Unmappable, canonical.Kind);
    }

    [Fact]
    public void InformationSchemaSpellingOfANativeType_StillMatchesByBaseName()
    {
        // information_schema.columns often reports "decimal" the same as the internal catalog would;
        // the point of this test is the (p,s) substitution surviving args in either order of size.
        var canonical = Dialect().ToCanonicalType("decimal(5,0)");

        Assert.Equal(CanonicalTypeKind.Decimal, canonical.Kind);
        Assert.Equal(5, canonical.Precision);
        Assert.Equal(0, canonical.Scale);
    }

    [Fact]
    public void QuoteIdentifier_UsesTheBacktickStyle_AndDoublesAnEmbeddedBacktick()
    {
        var dialect = Dialect();

        Assert.Equal("`Orders`", dialect.QuoteIdentifier("Orders"));
        // Each embedded backtick doubles, then the whole identifier is wrapped in one more pair.
        Assert.Equal("```a``b```", dialect.QuoteIdentifier("`a`b`"));
    }

    [Fact]
    public void ParameterReference_UsesTheConfiguredPrefix()
    {
        Assert.Equal("@p", Dialect().ParameterReference("p"));
    }

    [Fact]
    public void RenderTieSafeRowLimit_LimitOffsetStyle_RendersAPlainLimitSuffix()
    {
        var (prefix, suffix) = Dialect().RenderTieSafeRowLimit("batchSize");

        Assert.Equal("", prefix);
        Assert.Contains("LIMIT @batchSize", suffix);
    }

    [Fact]
    public void RenderTieSafeRowLimit_OffsetFetchStyle_UsesTheBaseClassesWithTiesForm()
    {
        const string yaml = """
            id: firebird.generic
            displayName: Firebird (generic)
            library: firebird-client
            dialect:
              quoteIdentifier: doubleQuote
              parameterPrefix: "@"
              rowLimit: offsetFetch
              catalog: informationSchema
            capabilities:
              readers: [Watermark]
              staging: [StagingTable]
              writers: [DeleteInsert]
            """;

        var (prefix, suffix) = Dialect(yaml).RenderTieSafeRowLimit("batchSize");

        Assert.Equal("", prefix);
        Assert.Contains("WITH TIES", suffix);
    }

    [Fact]
    public void RenderColumnType_IsNotSupported()
    {
        var dialect = Dialect();
        var canonical = dialect.ToCanonicalType("int");

        Assert.Throws<NotSupportedException>(() => dialect.RenderColumnType(canonical));
    }

    [Fact]
    public async Task UseDatabaseAsync_WhenTheEngineDoesNotSupportChangingIt_ThrowsBeforeTouchingTheConnection()
    {
        const string yaml = """
            id: oracle.generic
            displayName: Oracle (generic)
            library: oracle-managed-data-access
            dialect:
              quoteIdentifier: doubleQuote
              parameterPrefix: ":"
              rowLimit: offsetFetch
              catalog: informationSchema
              supportsChangeDatabase: false
            capabilities:
              readers: [Watermark]
              staging: [StagingTable]
              writers: [DeleteInsert]
            """;

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => Dialect(yaml).UseDatabaseAsync(null!, "other", CancellationToken.None));

        Assert.Contains("does not support switching database", ex.Message);
    }

    /// <summary>The other half of the same fix: an empty database is "nothing to switch to" (see
    /// <c>DbDataSync.Core.Config.TableRef.Database</c>'s own doc comment), not a request this engine
    /// refuses — so it never reaches the <c>supportsChangeDatabase: false</c> check at all. Passes a
    /// null connection deliberately: if this touched the connection in any way, it would throw a
    /// NullReferenceException instead of completing.</summary>
    [Fact]
    public async Task UseDatabaseAsync_WithAnEmptyDatabase_NeverReachesTheUnsupportedCheck()
    {
        const string yaml = """
            id: oracle.generic
            displayName: Oracle (generic)
            library: oracle-managed-data-access
            dialect:
              quoteIdentifier: doubleQuote
              parameterPrefix: ":"
              rowLimit: offsetFetch
              catalog: informationSchema
              supportsChangeDatabase: false
            capabilities:
              readers: [Watermark]
              staging: [StagingTable]
              writers: [DeleteInsert]
            """;

        await Dialect(yaml).UseDatabaseAsync(null!, "", CancellationToken.None);  // does not throw
    }
}
