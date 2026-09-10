using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.Serialization;

namespace DbDataSync.Core.Config;

/// <summary>
/// Reads and writes an <see cref="AfterChangeStrategy"/> in YAML, keyed by the same <c>mode</c>
/// discriminator its JSON form uses — phase 125. Mirrors <see cref="DeleteGuardYamlConverter"/> exactly;
/// see its own doc comment (and <see cref="BatchReloadSegmentYamlConverter"/>'s, the original) for why
/// this is hand-written rather than configured.
/// </summary>
public sealed class AfterChangeStrategyYamlConverter : IYamlTypeConverter
{
    public bool Accepts(Type type) => typeof(AfterChangeStrategy).IsAssignableFrom(type);

    public object ReadYaml(IParser parser, Type type, ObjectDeserializer rootDeserializer)
    {
        parser.Consume<MappingStart>();

        string? mode = null;
        while (!parser.TryConsume<MappingEnd>(out _))
        {
            var key = parser.Consume<Scalar>().Value;
            var scalar = parser.Consume<Scalar>();
            if (string.Equals(key, "mode", StringComparison.OrdinalIgnoreCase))
                mode = scalar.Style == ScalarStyle.Plain && scalar.Value is "" or "~" or "null" ? null : scalar.Value;
        }

        return mode switch
        {
            "none" => new NoAfterChangeStrategy(),
            "any" => new AfterAnyChangeStrategy(),
            null => throw new YamlException("An after-change strategy is missing its 'mode'."),
            _ => throw new YamlException($"Unknown after-change strategy mode '{mode}' (expected none or any)."),
        };
    }

    public void WriteYaml(IEmitter emitter, object? value, Type type, ObjectSerializer serializer)
    {
        var strategy = (AfterChangeStrategy)value!;
        emitter.Emit(new MappingStart());
        emitter.Emit(new Scalar("mode"));
        emitter.Emit(new Scalar(strategy switch
        {
            NoAfterChangeStrategy => "none",
            AfterAnyChangeStrategy => "any",
            _ => throw new YamlException($"No YAML form is defined for after-change strategy type '{strategy.GetType().Name}'."),
        }));
        emitter.Emit(new MappingEnd());
    }
}
