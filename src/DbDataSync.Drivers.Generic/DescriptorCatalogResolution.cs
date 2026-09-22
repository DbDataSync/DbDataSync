namespace DbDataSync.Drivers.Generic;

/// <summary>
/// Resolves a <c>driver.yaml</c> descriptor's <c>catalog</c> strategy to the actual
/// <see cref="IDescriptorCatalog"/> a driver's spec carries — shared by every kind's own
/// <c>FromDescriptor</c> (<see cref="GenericDriver.FromDescriptor"/>, <c>JdbcGenericDriver.FromDescriptor</c>
/// in <c>DbDataSync.Drivers.Jdbc</c>), each of which supplies its own <paramref name="default"/> —
/// <c>InformationSchemaQueries</c> for ADO.NET, <c>JdbcCatalog.Instance</c> (backed by
/// <c>java.sql.DatabaseMetaData</c>) for JDBC. <c>catalog: query</c> overrides either one identically,
/// which is the whole point of keeping this in one place rather than duplicating it per kind.
/// <para>
/// Takes plain strings rather than <c>DriverDescriptorYaml</c>/<c>MetadataQueriesYaml</c> directly:
/// those live in <c>DbDataSync.Drivers.Descriptor</c>, which references this project, not the other way
/// round.
/// </para>
/// </summary>
public static class DescriptorCatalogResolution
{
    public static IDescriptorCatalog Resolve(
        string driverId, string? strategy, string? tableQuery, string? columnQuery, IDescriptorCatalog @default) =>
        strategy switch
        {
            null or "default" => @default,
            "query" => new QueryCatalog(
                tableQuery ?? throw MissingQueryField(driverId, "tableQuery"),
                columnQuery ?? throw MissingQueryField(driverId, "columnQuery")),
            var other => throw new NotSupportedException(
                $"Driver '{driverId}': catalog strategy '{other}' is not supported — 'default' (this " +
                "driver kind's own answer) and 'query' are."),
        };

    private static NotSupportedException MissingQueryField(string driverId, string field) =>
        new($"Driver '{driverId}': catalog strategy 'query' requires a metadataQueries block with '{field}'.");
}
