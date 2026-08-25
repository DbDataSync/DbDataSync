using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace DataSync.Core.Config;

internal static class YamlConfigSerializer
{
    private static readonly ISerializer Serializer = new SerializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .ConfigureDefaultValuesHandling(DefaultValuesHandling.OmitDefaults)
        .Build();

    private static readonly IDeserializer Deserializer = new DeserializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .Build();

    public static string Serialize<T>(T value) => Serializer.Serialize(value);

    public static T Deserialize<T>(string yaml) => Deserializer.Deserialize<T>(yaml);
}
