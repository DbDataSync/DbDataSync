using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace DbDataSync.Core.Config;

internal static class YamlConfigSerializer
{
    // A segment is a sealed hierarchy, which neither half of YamlDotNet handles on its own — see
    // BatchReloadSegmentYamlConverter for why this is a hand-written converter rather than
    // configuration.
    private static readonly BatchReloadSegmentYamlConverter Segments = new();

    // Phase 125's own abstract hierarchies, same reasoning as Segments above.
    private static readonly DeleteGuardYamlConverter DeleteGuards = new();
    private static readonly AfterChangeStrategyYamlConverter AfterChangeStrategies = new();

    private static readonly ISerializer Serializer = new SerializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .ConfigureDefaultValuesHandling(DefaultValuesHandling.OmitDefaults)
        .WithTypeConverter(Segments)
        .WithTypeConverter(DeleteGuards)
        .WithTypeConverter(AfterChangeStrategies)
        .Build();

    private static readonly IDeserializer Deserializer = new DeserializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .WithTypeConverter(Segments)
        .WithTypeConverter(DeleteGuards)
        .WithTypeConverter(AfterChangeStrategies)
        // A key a past build wrote but this one no longer models (e.g. the per-stage `parallelism`
        // that `ChangeProcessingConfig.DegreeOfParallelism` replaced) must not stop an existing
        // task.yaml from loading — it is dropped here and disappears on the next save. This is not
        // the weakening BatchReloadSegmentYamlConverter's comment warns against: that is specifically
        // about a segment's discriminator, which still travels through its own hand-written converter.
        .IgnoreUnmatchedProperties()
        .Build();

    public static string Serialize<T>(T value) => Serializer.Serialize(value);

    public static T Deserialize<T>(string yaml) => Deserializer.Deserialize<T>(yaml);
}
