using DbDataSync.Drivers.Abstractions;
using DbDataSync.Drivers.Generic;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace DbDataSync.Drivers.Descriptor;

/// <summary>Parses <c>driver.yaml</c> and stands up the <see cref="GenericDriverSpec"/> it describes —
/// everything except resolving the library's factory, which needs a <c>LibraryRegistry</c> this
/// project deliberately does not depend on (see <see cref="DriverLoader"/>, which does both).</summary>
public static class DriverDescriptorReader
{
    private static readonly IDeserializer Deserializer = new DeserializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .WithTypeConverter(new TypeMapEntryYamlConverter())
        .Build();

    public static DriverDescriptorYaml Read(string path) => Deserialize(File.ReadAllText(path));

    public static DriverDescriptorYaml Deserialize(string yaml) => Deserializer.Deserialize<DriverDescriptorYaml>(yaml);

    /// <summary>Everything a <see cref="GenericDriver"/> needs except the library's factory itself.</summary>
    public static GenericDriverSpec ToSpec(DriverDescriptorYaml descriptor, System.Data.Common.DbProviderFactory factory)
    {
        var dialect = new DescriptorDialect(descriptor.Dialect, descriptor.TypeMap);

        IDescriptorCatalog catalog = descriptor.Dialect.Catalog switch
        {
            "informationSchema" => new InformationSchemaQueries(dialect),
            "query" => new QueryCatalog(
                (descriptor.MetadataQueries ?? throw new NotSupportedException(
                    $"Driver '{descriptor.Id}': catalog strategy 'query' requires a metadataQueries " +
                    "block (tableQuery, columnQuery).")).TableQuery,
                descriptor.MetadataQueries.ColumnQuery),
            var other => throw new NotSupportedException(
                $"Driver '{descriptor.Id}': catalog strategy '{other}' is not supported — " +
                "'informationSchema' and 'query' are."),
        };

        var keys = descriptor.Dialect.ConnectionStringKeys is { } k
            ? new GenericConnectionStringKeys(k.Host, k.Port, k.Database, k.Username, k.Password, k.ConnectTimeout, k.IntegratedSecurity)
            : new GenericConnectionStringKeys();

        return new GenericDriverSpec(
            descriptor.Id,
            dialect,
            factory,
            catalog,
            Readers: descriptor.Capabilities.Readers,
            Staging: descriptor.Capabilities.Staging,
            Writers: descriptor.Capabilities.Writers,
            ConnectionStringKeys: keys,
            DefaultDatabase: descriptor.Dialect.DefaultDatabase,
            DefaultPort: descriptor.Dialect.DefaultPort,
            DisplayName: descriptor.DisplayName);
    }
}
