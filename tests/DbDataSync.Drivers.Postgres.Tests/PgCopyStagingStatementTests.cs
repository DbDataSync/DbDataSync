using DbDataSync.Core.Config;
using DbDataSync.Core.Sql;
using DbDataSync.Drivers.Abstractions;
using DbDataSync.Drivers.Generic;
using NpgsqlTypes;
using Xunit;

namespace DbDataSync.Drivers.Postgres.Tests;

/// <summary>
/// The two parts of phase 38's <c>COPY</c> staging that are wrong-able without a server: which columns
/// the stream carries and in what order, and which <see cref="NpgsqlDbType"/> each one is written as.
/// <para>
/// Both matter more here than they would for a parameterised <c>INSERT</c>. Binary <c>COPY</c> is
/// positional and untyped on the wire — the server takes whatever arrives in the order the command
/// named, and converts nothing — so a column list off by one, or a type resolved to the wrong enum,
/// does not fail loudly at the first row. It silently writes the wrong thing, or fails several columns
/// later with a message about the wrong column.
/// </para>
/// </summary>
public sealed class PgCopyStagingStatementTests
{
    private static readonly SqlDialect Dialect = PostgresDialect.Instance;

    [Fact]
    public void TheCopyCommand_NamesEveryMappedColumnThenTheOperationMarker()
    {
        var sql = PgCopyStagingProvider.BuildCopyCommand(
            Dialect, "\"public\".\"DS_STG_x\"", ["id", "name"], includeChangeOrdering: false);

        Assert.Equal(
            "COPY \"public\".\"DS_STG_x\" (\"id\", \"name\", \"__Operation\") FROM STDIN (FORMAT BINARY)",
            sql);
    }

    /// <summary>Phase 132's two columns sit between the mapped ones and the marker, which is the order
    /// the writer streams its cells in — the one place an off-by-one would land every value in the
    /// column next door.</summary>
    [Fact]
    public void TheCopyCommand_WithAnOrderedBatch_CarriesBothOrderingColumnsBeforeTheMarker()
    {
        var sql = PgCopyStagingProvider.BuildCopyCommand(
            Dialect, "\"public\".\"DS_STG_x\"", ["id"], includeChangeOrdering: true);

        Assert.Equal(
            "COPY \"public\".\"DS_STG_x\" (\"id\", \"__DS_ChangeOrdering\", \"__DS_ChangedAtUtc\", \"__Operation\") " +
            "FROM STDIN (FORMAT BINARY)",
            sql);
    }

    /// <summary>
    /// The ordinal is never in the list. It is <c>GENERATED ALWAYS AS IDENTITY</c> in the DDL, so the
    /// engine fills it in as each row lands — naming it here would make the server reject the copy
    /// outright, and leaving it out is what makes a chunked apply's range work the same for both
    /// staging providers.
    /// </summary>
    [Fact]
    public void TheCopyCommand_NeverNamesTheGeneratedOrdinal()
    {
        var sql = PgCopyStagingProvider.BuildCopyCommand(
            Dialect, "\"public\".\"DS_STG_x\"", ["id"], includeChangeOrdering: true);

        Assert.DoesNotContain(BatchInsertStagingProvider.OrdinalColumn, sql);
    }

    /// <summary>The DDL is the generic provider's own builder, not a second one — the two staging
    /// providers create the identical table, which is what makes a mapping movable between them.</summary>
    [Fact]
    public void TheStagingTable_IsTheSameDdlTheGenericProviderBuilds()
    {
        var typeByName = new Dictionary<string, string> { ["id"] = "integer", ["name"] = "text" };

        Assert.Contains(
            "\"id\" integer NULL, \"name\" text NULL, \"__Operation\" char(1) NOT NULL",
            StagingStatement.BuildCreate(Dialect, "\"public\".\"DS_STG_x\"", ["id", "name"], typeByName));
    }

    [Theory]
    // The information_schema spellings, which is what PostgresCatalog returns...
    [InlineData("integer", NpgsqlDbType.Integer)]
    [InlineData("bigint", NpgsqlDbType.Bigint)]
    [InlineData("smallint", NpgsqlDbType.Smallint)]
    [InlineData("boolean", NpgsqlDbType.Boolean)]
    [InlineData("double precision", NpgsqlDbType.Double)]
    [InlineData("character varying", NpgsqlDbType.Varchar)]
    [InlineData("timestamp without time zone", NpgsqlDbType.Timestamp)]
    [InlineData("timestamp with time zone", NpgsqlDbType.TimestampTz)]
    [InlineData("time without time zone", NpgsqlDbType.Time)]
    // ... and the internal ones, which is what reaches here from a catalog query that read pg_type.
    [InlineData("int4", NpgsqlDbType.Integer)]
    [InlineData("int8", NpgsqlDbType.Bigint)]
    [InlineData("bool", NpgsqlDbType.Boolean)]
    [InlineData("float8", NpgsqlDbType.Double)]
    [InlineData("varchar", NpgsqlDbType.Varchar)]
    [InlineData("timestamptz", NpgsqlDbType.TimestampTz)]
    // A length or precision is not part of the decision.
    [InlineData("numeric(18,2)", NpgsqlDbType.Numeric)]
    [InlineData("character varying(50)", NpgsqlDbType.Varchar)]
    // Anything unrecognised is text, which is what an unmapped Postgres type is reachable as.
    [InlineData("citext", NpgsqlDbType.Text)]
    [InlineData("jsonb", NpgsqlDbType.Text)]
    public void AColumnsDeclaredType_ResolvesToTheTypeItIsWrittenAs(string nativeType, NpgsqlDbType expected) =>
        Assert.Equal(expected, PostgresNpgsqlTypes.Of(nativeType));

    /// <summary>
    /// The three columns staging adds itself are resolved through the same table as a mapped one, from
    /// the same dialect properties their DDL came from — so a dialect that changed one of those types
    /// could not leave the writer sending the old one.
    /// </summary>
    [Fact]
    public void StagingsOwnColumns_ResolveFromTheSameDialectPropertiesTheirDdlUses()
    {
        Assert.Equal(NpgsqlDbType.Varchar, PostgresNpgsqlTypes.Of(Dialect.ChangeOrderingColumnType));
        Assert.Equal(NpgsqlDbType.Timestamp, PostgresNpgsqlTypes.Of(Dialect.ChangedAtColumnType));
        Assert.Equal(NpgsqlDbType.Char, PostgresNpgsqlTypes.Of(Dialect.OperationMarkerColumnType));
    }

    /// <summary>A connection that is not Npgsql's cannot be bulk-loaded through Npgsql's importer, and
    /// the message says which Kind can take it instead rather than throwing a cast error.</summary>
    [Fact]
    public async Task ANonNpgsqlConnection_IsRefusedByNameRatherThanCastError()
    {
        var provider = new PgCopyStagingProvider(Dialect, PostgresCatalog.Instance);
        using var connection = new Microsoft.Data.SqlClient.SqlConnection();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => provider.StageAsync(
            connection, new TableRef { ConnectionName = "t", Database = "d", Schema = "public", Table = "t" },
            Empty(), [new() { SourceColumn = "id", TargetColumn = "id" }], "m", [], new Dictionary<string, string>(),
            CancellationToken.None));

        Assert.Contains(PostgresDriverKinds.StagingTable, ex.Message);
        Assert.Contains("SqlConnection", ex.Message);
    }

    private static async IAsyncEnumerable<ChangeRow> Empty()
    {
        await Task.CompletedTask;
        yield break;
    }
}
