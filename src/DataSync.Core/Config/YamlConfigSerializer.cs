using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace DataSync.Core.Config;

internal static class YamlConfigSerializer
{
    // A segment is a sealed hierarchy, which neither half of YamlDotNet handles on its own — see
    // BatchReloadSegmentYamlConverter for why this is a hand-written converter rather than
    // configuration.
    private static readonly BatchReloadSegmentYamlConverter Segments = new();

    private static readonly ISerializer Serializer = new SerializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .ConfigureDefaultValuesHandling(DefaultValuesHandling.OmitDefaults)
        .WithTypeConverter(Segments)
        .Build();

    private static readonly IDeserializer Deserializer = new DeserializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .WithTypeConverter(Segments)
        .Build();

    public static string Serialize<T>(T value) => Serializer.Serialize(value);

    public static T Deserialize<T>(string yaml) => Deserializer.Deserialize<T>(yaml);
}
