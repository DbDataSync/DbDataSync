using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.Serialization;

namespace DbDataSync.Drivers.Descriptor;

/// <summary>
/// Reads a <c>typeMap</c> value in either of its two shapes — a bare scalar (<c>datetime: Timestamp</c>)
/// or a mapping (<c>"decimal(p,s)": { kind: Decimal, precision: p, scale: s }</c>) — into one
/// <see cref="TypeMapEntryYaml"/>. YamlDotNet's own object deserialisation can't do this: it commits to
/// one node kind (scalar or mapping) per property based on the declared .NET type, and offers no
/// built-in "try scalar, else mapping" fallback the way <c>BatchReloadSegmentYamlConverter</c> needed
/// for a discriminated union — this is the same shape of problem, one level simpler since there is no
/// discriminator to read, just two literal node kinds.
/// <para>Write-only in the other direction is not needed: <c>driver.yaml</c> is authored by
/// <c>dbdatasync driver install</c> writing a skeleton (109d's own scope, hand-edited after) or by an
/// operator directly — this repo never re-serialises a descriptor it read.</para>
/// </summary>
public sealed class TypeMapEntryYamlConverter : IYamlTypeConverter
{
    public bool Accepts(Type type) => type == typeof(TypeMapEntryYaml);

    public object ReadYaml(IParser parser, Type type, ObjectDeserializer rootDeserializer)
    {
        if (parser.TryConsume<Scalar>(out var scalar))
            return new TypeMapEntryYaml { Kind = scalar.Value };

        parser.Consume<MappingStart>();
        var entry = new TypeMapEntryYaml { Kind = "" };
        while (!parser.TryConsume<MappingEnd>(out _))
        {
            var key = parser.Consume<Scalar>().Value;
            var value = parser.Consume<Scalar>().Value;
            switch (key.ToLowerInvariant())
            {
                case "kind": entry.Kind = value; break;
                case "precision": entry.Precision = value; break;
                case "scale": entry.Scale = value; break;
                case "length": entry.Length = value; break;
                case "max": entry.Max = bool.Parse(value); break;
                case "unicode": entry.Unicode = bool.Parse(value); break;
                default: throw new YamlException($"Unknown typeMap entry field '{key}'.");
            }
        }

        if (entry.Kind.Length == 0)
            throw new YamlException("A typeMap entry mapping is missing its required 'kind'.");
        return entry;
    }

    public void WriteYaml(IEmitter emitter, object? value, Type type, ObjectSerializer serializer) =>
        throw new NotSupportedException(
            "driver.yaml is authored by an operator or `driver install`'s skeleton, never re-serialised by this reader.");
}
