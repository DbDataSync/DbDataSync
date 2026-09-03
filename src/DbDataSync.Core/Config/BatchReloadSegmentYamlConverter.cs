using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.Serialization;

namespace DbDataSync.Core.Config;

/// <summary>
/// Reads and writes a <see cref="BatchReloadSegment"/> in YAML, keyed by the same <c>mode</c>
/// discriminator its JSON form uses.
/// <para>
/// Written by hand rather than configured, because neither half comes for free. YamlDotNet has no
/// notion of the <c>[JsonDerivedType]</c> attributes the JSON side is driven by, so it would emit a
/// derived record's fields with nothing saying which record they belong to — and on the way back in it
/// cannot construct an abstract type at all. The alternative,
/// <c>IgnoreUnmatchedProperties</c> plus a synthetic settable discriminator property, would weaken
/// validation for *every* config file this serializer touches so that one type could round-trip; a
/// mistyped key in a replication would then be silently ignored rather than reported.
/// </para>
/// <para>
/// The written shape is deliberately the same as the JSON one — <c>mode: range</c> and the record's
/// own fields, camel-cased — so a segment reads the same wherever an operator meets it.
/// </para>
/// </summary>
public sealed class BatchReloadSegmentYamlConverter : IYamlTypeConverter
{
    /// <summary>
    /// The base type *and* every derived record, because the two directions ask different questions:
    /// deserialization offers the declared element type (<see cref="BatchReloadSegment"/>) while
    /// serialization offers the runtime one (<c>RangeSegment</c>). Accepting only the base produces a
    /// converter that reads but does not write — which shows up as "a segment is missing its 'mode'"
    /// on the way back in, having silently written the fields without one.
    /// </summary>
    public bool Accepts(Type type) => typeof(BatchReloadSegment).IsAssignableFrom(type);

    public object ReadYaml(IParser parser, Type type, ObjectDeserializer rootDeserializer)
    {
        parser.Consume<MappingStart>();

        string? mode = null;
        var scalars = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        var sequences = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

        while (!parser.TryConsume<MappingEnd>(out _))
        {
            var key = parser.Consume<Scalar>().Value;

            if (parser.TryConsume<SequenceStart>(out _))
            {
                var values = new List<string>();
                while (!parser.TryConsume<SequenceEnd>(out _))
                    values.Add(parser.Consume<Scalar>().Value);
                sequences[key] = values;
                continue;
            }

            var scalar = parser.Consume<Scalar>();
            // A null in YAML arrives as an empty or `~`/`null` scalar with no quoting; a quoted empty
            // string is a real empty string and must not become null.
            var value = scalar.Style == ScalarStyle.Plain && scalar.Value is "" or "~" or "null"
                ? null
                : scalar.Value;

            if (string.Equals(key, "mode", StringComparison.OrdinalIgnoreCase))
                mode = value;
            else
                scalars[key] = value;
        }

        string Required(string name) =>
            scalars.TryGetValue(name, out var value) && value is not null
                ? value
                : throw new YamlException($"A '{mode}' segment is missing its required '{name}'.");

        return mode switch
        {
            "full" => new FullSegment(),
            "list" => new ListSegment(
                Required("column"),
                sequences.TryGetValue("values", out var values) ? values : []),
            "range" => new RangeSegment(
                Required("column"), Required("rangeMin"), Required("rangeMax"),
                scalars.GetValueOrDefault("label")),
            "auto" => new AutoSegment(
                Required("column"),
                int.TryParse(Required("bucketCount"), out var buckets)
                    ? buckets
                    : throw new YamlException("An 'auto' segment's 'bucketCount' must be a number.")),
            "custom" => new CustomSegment(Required("strategyName")),
            null => throw new YamlException("A segment is missing its 'mode'."),
            _ => throw new YamlException(
                $"Unknown segment mode '{mode}' (expected full, list, range, auto or custom)."),
        };
    }

    public void WriteYaml(IEmitter emitter, object? value, Type type, ObjectSerializer serializer)
    {
        var segment = (BatchReloadSegment)value!;
        emitter.Emit(new MappingStart());

        switch (segment)
        {
            case FullSegment:
                Pair(emitter, "mode", "full");
                break;

            case ListSegment list:
                Pair(emitter, "mode", "list");
                Pair(emitter, "column", list.Column);
                emitter.Emit(new Scalar("values"));
                emitter.Emit(new SequenceStart(null, null, true, SequenceStyle.Block));
                foreach (var item in list.Values)
                    emitter.Emit(new Scalar(item));
                emitter.Emit(new SequenceEnd());
                break;

            case RangeSegment range:
                Pair(emitter, "mode", "range");
                Pair(emitter, "column", range.Column);
                Pair(emitter, "rangeMin", range.RangeMin);
                Pair(emitter, "rangeMax", range.RangeMax);
                if (range.Label is not null)
                    Pair(emitter, "label", range.Label);
                break;

            case AutoSegment auto:
                Pair(emitter, "mode", "auto");
                Pair(emitter, "column", auto.Column);
                Pair(emitter, "bucketCount", auto.BucketCount.ToString());
                break;

            case CustomSegment custom:
                Pair(emitter, "mode", "custom");
                Pair(emitter, "strategyName", custom.StrategyName);
                break;

            default:
                throw new YamlException($"No YAML form is defined for segment type '{segment.GetType().Name}'.");
        }

        emitter.Emit(new MappingEnd());
    }

    private static void Pair(IEmitter emitter, string key, string value)
    {
        emitter.Emit(new Scalar(key));
        emitter.Emit(new Scalar(value));
    }
}
