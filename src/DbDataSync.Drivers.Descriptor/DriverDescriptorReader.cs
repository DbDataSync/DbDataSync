using DbDataSync.Drivers.Generic;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace DbDataSync.Drivers.Descriptor;

/// <summary>Parses <c>driver.yaml</c> and stands up the <see cref="GenericDriverSpec"/> it describes —
/// everything except resolving the provider factory, which needs a <c>ProviderRegistry</c> this
/// project deliberately does not depend on (see <see cref="DriverLoader"/>, which does both).</summary>
public static class DriverDescriptorReader
{
    private static readonly IDeserializer Deserializer = new DeserializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .WithTypeConverter(new TypeMapEntryYamlConverter())
        .Build();

    public static DriverDescriptorYaml Read(string path) => Deserialize(File.ReadAllText(path));

    public static DriverDescriptorYaml Deserialize(string yaml) => Deserializer.Deserialize<DriverDescriptorYaml>(yaml);

    /// <summary>Everything a <see cref="GenericDriver"/> needs except the provider factory itself.</summary>
    public static GenericDriverSpec ToSpec(DriverDescriptorYaml descriptor, System.Data.Common.DbProviderFactory factory)
    {
        if (descriptor.Dialect.Catalog != "informationSchema")
        {
            throw new NotSupportedException(
                $"Driver '{descriptor.Id}': catalog strategy '{descriptor.Dialect.Catalog}' is not " +
                "supported — only 'informationSchema' is, for now.");
        }

        var dialect = new DescriptorDialect(descriptor.Dialect, descriptor.TypeMap);
        var keys = descriptor.Dialect.ConnectionStringKeys is { } k
            ? new GenericConnectionStringKeys(k.Host, k.Port, k.Database, k.Username, k.Password, k.ConnectTimeout, k.IntegratedSecurity)
            : new GenericConnectionStringKeys();

        return new GenericDriverSpec(
            descriptor.Id,
            dialect,
            factory,
            new InformationSchemaQueries(dialect),
            Readers: descriptor.Capabilities.Readers,
            Staging: descriptor.Capabilities.Staging,
            Writers: descriptor.Capabilities.Writers,
            ConnectionStringKeys: keys,
            DefaultDatabase: descriptor.Dialect.DefaultDatabase,
            DefaultPort: descriptor.Dialect.DefaultPort,
            DisplayName: descriptor.DisplayName);
    }
}
