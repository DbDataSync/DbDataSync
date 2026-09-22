using System.Reflection;
using DbDataSync.Drivers.Abstractions;
using DbDataSync.Drivers.Generic;
using DbDataSync.Libraries;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace DbDataSync.Drivers.Descriptor;

/// <summary>Parses <c>driver.yaml</c> and stands up the <see cref="IDriver"/> it describes.</summary>
public static class DriverDescriptorReader
{
    private static readonly IDeserializer Deserializer = new DeserializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .WithTypeConverter(new TypeMapEntryYamlConverter())
        .Build();

    public static DriverDescriptorYaml Read(string path) => Deserialize(File.ReadAllText(path));

    public static DriverDescriptorYaml Deserialize(string yaml) => Deserializer.Deserialize<DriverDescriptorYaml>(yaml);

    /// <summary>
    /// Phase 168V. <see cref="DriverDescriptorYaml.Base"/> omitted (the common case) builds a
    /// <see cref="GenericDriver"/> the existing way, through <paramref name="libraries"/> and a
    /// <see cref="System.Data.Common.DbProviderFactory"/> — every pre-phase-168V <c>driver.yaml</c> keeps
    /// working unchanged. A named <c>base</c> is resolved via reflection to a public static
    /// <c>FromDescriptor(DriverDescriptorYaml)</c> on the type it names — the same assembly-qualified-name
    /// shape <c>library.json</c>'s own <c>factoryType</c> already uses — so a third kind never needs this
    /// method to change, only a new <c>driver.yaml</c> naming it.
    /// </summary>
    public static IDriver BuildDriver(DriverDescriptorYaml descriptor, LibraryRegistry libraries)
    {
        if (descriptor.Base is null)
            return new GenericDriver(ToSpec(descriptor, libraries.GetFactory(descriptor.Library)));

        var type = Type.GetType(descriptor.Base)
            ?? throw new NotSupportedException(
                $"Driver '{descriptor.Id}': base '{descriptor.Base}' could not be resolved to a type. " +
                "Use an assembly-qualified name, e.g. 'DbDataSync.Drivers.Jdbc.JdbcGenericDriver, DbDataSync.Drivers.Jdbc'.");

        var method = type.GetMethod("FromDescriptor", BindingFlags.Public | BindingFlags.Static, [typeof(DriverDescriptorYaml)])
            ?? throw new NotSupportedException(
                $"Driver '{descriptor.Id}': base '{descriptor.Base}' has no public static " +
                "FromDescriptor(DriverDescriptorYaml) method.");

        return (IDriver)method.Invoke(null, [descriptor])!;
    }

    /// <summary>Everything a <see cref="GenericDriver"/> needs except the library's factory itself —
    /// separate from <see cref="BuildDriver"/> so a caller that already has a resolved factory (or wants
    /// to inspect the spec before constructing) still can.</summary>
    public static GenericDriverSpec ToSpec(DriverDescriptorYaml descriptor, System.Data.Common.DbProviderFactory factory)
    {
        var dialect = new DescriptorDialect(descriptor.Dialect, descriptor.TypeMap);
        var catalog = DescriptorCatalogResolution.Resolve(
            descriptor.Id, descriptor.Dialect.Catalog, descriptor.MetadataQueries?.TableQuery,
            descriptor.MetadataQueries?.ColumnQuery, @default: new InformationSchemaQueries(dialect));

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
