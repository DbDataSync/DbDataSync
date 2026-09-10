using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.Serialization;

namespace DbDataSync.Core.Config;

/// <summary>
/// Reads and writes a <see cref="DeleteGuard"/> in YAML, keyed by the same <c>mode</c> discriminator its
/// JSON form uses — phase 125, once <see cref="ReconcileConfig.DeleteGuard"/> gives a guard a persisted
/// home. Hand-written for the identical reason <see cref="BatchReloadSegmentYamlConverter"/> is: YamlDotNet
/// has no notion of <c>[JsonDerivedType]</c>, so it can neither write a derived record's discriminator
/// nor construct the abstract base type on the way back in.
/// </summary>
public sealed class DeleteGuardYamlConverter : IYamlTypeConverter
{
    public bool Accepts(Type type) => typeof(DeleteGuard).IsAssignableFrom(type);

    public object ReadYaml(IParser parser, Type type, ObjectDeserializer rootDeserializer)
    {
        parser.Consume<MappingStart>();

        string? mode = null;
        var scalars = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

        while (!parser.TryConsume<MappingEnd>(out _))
        {
            var key = parser.Consume<Scalar>().Value;
            var scalar = parser.Consume<Scalar>();
            var value = scalar.Style == ScalarStyle.Plain && scalar.Value is "" or "~" or "null" ? null : scalar.Value;

            if (string.Equals(key, "mode", StringComparison.OrdinalIgnoreCase))
                mode = value;
            else
                scalars[key] = value;
        }

        return mode switch
        {
            "none" => new NoneDeleteGuard(),
            "ratio" => new RatioDeleteGuard(
                scalars.TryGetValue("maxRatio", out var raw) && raw is not null && double.TryParse(raw, out var parsed)
                    ? parsed
                    : 0.5),
            null => throw new YamlException("A delete guard is missing its 'mode'."),
            _ => throw new YamlException($"Unknown delete guard mode '{mode}' (expected none or ratio)."),
        };
    }

    public void WriteYaml(IEmitter emitter, object? value, Type type, ObjectSerializer serializer)
    {
        var guard = (DeleteGuard)value!;
        emitter.Emit(new MappingStart());

        switch (guard)
        {
            case NoneDeleteGuard:
                Pair(emitter, "mode", "none");
                break;

            case RatioDeleteGuard ratio:
                Pair(emitter, "mode", "ratio");
                Pair(emitter, "maxRatio", ratio.MaxRatio.ToString("R"));
                break;

            default:
                throw new YamlException($"No YAML form is defined for delete guard type '{guard.GetType().Name}'.");
        }

        emitter.Emit(new MappingEnd());
    }

    private static void Pair(IEmitter emitter, string key, string value)
    {
        emitter.Emit(new Scalar(key));
        emitter.Emit(new Scalar(value));
    }
}
