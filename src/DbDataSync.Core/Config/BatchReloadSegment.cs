using System.Text.Json;
using System.Text.Json.Serialization;

namespace DbDataSync.Core.Config;

/// <summary>
/// Describes which slice of a source table one batch-reload unit of work covers. Engine-neutral: a
/// segment is a column plus bounds, never a SQL fragment — each driver renders its own predicate (see
/// DbDataSync.Drivers.MsSql.MsSqlSegmentScope for the v1 MSSQL rendering).
/// <para>
/// **In <c>DbDataSync.Core.Config</c> rather than beside the drivers that consume it**, because a table
/// mapping now stores its own default segmenting as one of these lists — and config cannot depend on
/// the driver layer, which depends on config. The type was always engine-neutral; this only moves it
/// to where its neutrality is structurally enforced.
/// </para>
/// <para>
/// A sealed hierarchy rather than one record with mode-dependent nullable fields: every mode carries
/// only the fields that are valid for it, so consumers can <c>switch</c> exhaustively instead of
/// relying on nullable-field discipline to know which combination is meaningful.
/// </para>
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "mode")]
[JsonDerivedType(typeof(FullSegment), "full")]
[JsonDerivedType(typeof(ListSegment), "list")]
[JsonDerivedType(typeof(RangeSegment), "range")]
[JsonDerivedType(typeof(AutoSegment), "auto")]
[JsonDerivedType(typeof(CustomSegment), "custom")]
public abstract record BatchReloadSegment
{
    /// <summary>Short human-readable description, used as a run's SegmentLabel and in log lines.</summary>
    public abstract string Describe();
}

/// <summary>The whole table — an unsegmented reload.</summary>
public sealed record FullSegment : BatchReloadSegment
{
    public override string Describe() => "full";
}

/// <summary>Rows whose <paramref name="Column"/> matches one of <paramref name="Values"/>.</summary>
/// <param name="Relationship">
/// Null for a column on the mapping's primary source (every segment before phase 195S). Non-null names
/// a declared <c>RelationshipConfig.Name</c> instead — <paramref name="Column"/> is then a column on
/// that relationship's own foreign table, not the primary one.
/// </param>
public sealed record ListSegment(string Column, IReadOnlyList<string> Values, string? Relationship = null) : BatchReloadSegment
{
    public override string Describe() => $"{Column} in ({string.Join(",", Values)})";

    // The compiler-generated record equality would compare Values by reference, so two segments
    // covering the same list — one just deserialized, one built in memory — would compare unequal.
    // That's the wrong answer for a value type describing a scope, and the sort of thing that quietly
    // breaks a dedupe or an assertion rather than failing loudly.
    public bool Equals(ListSegment? other) =>
        other is not null && Column == other.Column && Relationship == other.Relationship && Values.SequenceEqual(other.Values);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Column);
        hash.Add(Relationship);
        foreach (var value in Values)
            hash.Add(value);
        return hash.ToHashCode();
    }
}

/// <summary>Rows where <c>Column &gt;= RangeMin AND Column &lt; RangeMax</c> — half-open, so
/// consecutive ranges tile a value space without gaps or overlaps.</summary>
/// <param name="Label">
/// What to call this range, when something knows a better name for it than its bounds.
/// <para>
/// A segmenting strategy that divides a date column by calendar month has a name for each segment —
/// <c>"2024-03"</c> — and the generated <c>"OrderDate [2024-03-01, 2024-04-01)"</c> is strictly worse
/// to read in a run history. Optional, and null for every segment built any other way, so
/// <see cref="Describe"/> keeps producing exactly what it always did unless somebody supplied
/// something better.
/// </para>
/// </param>
/// <param name="Relationship">As <see cref="ListSegment"/>'s own field of the same name — phase 195S.</param>
public sealed record RangeSegment(string Column, string RangeMin, string RangeMax, string? Label = null, string? Relationship = null)
    : BatchReloadSegment
{
    public override string Describe() => Label ?? $"{Column} [{RangeMin}, {RangeMax})";
}

/// <summary>
/// A request to split <paramref name="Column"/>'s actual value range into <paramref name="BucketCount"/>
/// <see cref="RangeSegment"/>s. Consumed exactly once, at whatever point segment expansion happens
/// (<see cref="ISegmentExpandingReader"/>) — an <c>Auto</c> segment is never persisted as, or handed to
/// a reader/writer as, a runtime segment.
/// </summary>
/// <param name="Relationship">As <see cref="ListSegment"/>'s own field of the same name — phase 195S.</param>
public sealed record AutoSegment(string Column, int BucketCount, string? Relationship = null) : BatchReloadSegment
{
    public override string Describe() => $"{Column} auto/{BucketCount}";
}

/// <summary>
/// A request to run a bound segmenting strategy and use whichever candidates it flags as selected.
/// <para>
/// A marker, consumed exactly once — the same shape <see cref="AutoSegment"/> already establishes. It
/// is never persisted as, or handed to a reader or writer as, a runtime segment: whatever expands it
/// substitutes the strategy's own segments in its place first.
/// </para>
/// <para>
/// **The stored default is the strategy's name, not the segments it produced.** Freezing a list would
/// defeat the point of the feature: a strategy that generates "the last three months" has to be
/// re-evaluated against today every time it runs, not baked in on the day somebody configured it.
/// </para>
/// </summary>
/// <param name="Column">
/// Which column the strategy's ranges are over, on *this* table — not a property of the named
/// strategy itself (see <see cref="SegmentingStrategyConfig"/>'s own doc comment for why), so two
/// mappings referencing the same strategy by name can each name a different column.
/// <para>
/// Required for every <see cref="SegmentingStrategyKind"/> except <see cref="SegmentingStrategyKind.Script"/>
/// — a script already knows its own column (or generates <see cref="RangeSegment"/>s that need none),
/// the same exemption <c>SegmentingStrategyRunner</c> enforced when this lived on the strategy.
/// </para>
/// </param>
/// <param name="Relationship">As <see cref="ListSegment"/>'s own field of the same name — phase 195S.</param>
public sealed record CustomSegment(string StrategyName, string? Column = null, string? Relationship = null) : BatchReloadSegment
{
    public override string Describe() => Column is null ? $"custom/{StrategyName}" : $"custom/{StrategyName} over {Column}";
}

/// <summary>
/// The one place <see cref="BatchReloadSegment"/> is serialized, so the polymorphic discriminator is
/// configured identically at every round-trip point: the ephemeral <c>options["segment"]</c> channel
/// (injected per work item) and the array a bulk load request carries over HTTP.
/// <para>
/// The persisted form is no longer one of them. A mapping's stored segmenting is YAML now — see
/// <see cref="BatchReloadSegmentYamlConverter"/> — because it gained a real editor and stopped being
/// JSON somebody typed into an options bag.
/// </para>
/// <para>
/// Every method here goes through the <see cref="BatchReloadSegment"/>-typed generic overload
/// deliberately. Serializing a variable statically typed as a concrete derived record instead
/// (<c>Serialize(someFullSegment)</c>) makes System.Text.Json emit no discriminator at all, and
/// deserialization then fails at runtime rather than at the call site that caused it.
/// </para>
/// </summary>
public static class SegmentSerializer
{
    /// <summary>The well-known per-work-item options key carrying one serialized segment.</summary>
    public const string SegmentOptionKey = "segment";

    public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web);

    public static string Serialize(BatchReloadSegment segment) =>
        JsonSerializer.Serialize<BatchReloadSegment>(segment, Options);

    public static string SerializeMany(IReadOnlyList<BatchReloadSegment> segments) =>
        JsonSerializer.Serialize<IReadOnlyList<BatchReloadSegment>>(segments, Options);

    public static BatchReloadSegment Deserialize(string json) =>
        JsonSerializer.Deserialize<BatchReloadSegment>(json, Options)
        ?? throw new InvalidOperationException("Segment JSON deserialized to null.");

    public static IReadOnlyList<BatchReloadSegment> DeserializeMany(string json) =>
        JsonSerializer.Deserialize<IReadOnlyList<BatchReloadSegment>>(json, Options)
        ?? throw new InvalidOperationException("Segment array JSON deserialized to null.");

    /// <summary>Reads the single segment a work item injected into a reader/cache/writer's options,
    /// or null when the unit of work isn't segmented (an ordinary incremental pass, or a whole-table
    /// reload).</summary>
    public static BatchReloadSegment? ReadOptional(IReadOnlyDictionary<string, string> options) =>
        options.TryGetValue(SegmentOptionKey, out var json) && !string.IsNullOrWhiteSpace(json)
            ? Deserialize(json)
            : null;
}
