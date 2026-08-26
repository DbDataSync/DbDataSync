using System.Text.Json;
using System.Text.Json.Serialization;

namespace DataSync.Drivers.Abstractions;

/// <summary>
/// Describes which slice of a source table one batch-reload unit of work covers. Engine-neutral: a
/// segment is a column plus bounds, never a SQL fragment — each driver renders its own predicate (see
/// DataSync.Drivers.MsSql.MsSqlSegmentScope for the v1 MSSQL rendering).
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
public sealed record ListSegment(string Column, IReadOnlyList<string> Values) : BatchReloadSegment
{
    public override string Describe() => $"{Column} in ({string.Join(",", Values)})";

    // The compiler-generated record equality would compare Values by reference, so two segments
    // covering the same list — one just deserialized, one built in memory — would compare unequal.
    // That's the wrong answer for a value type describing a scope, and the sort of thing that quietly
    // breaks a dedupe or an assertion rather than failing loudly.
    public bool Equals(ListSegment? other) =>
        other is not null && Column == other.Column && Values.SequenceEqual(other.Values);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Column);
        foreach (var value in Values)
            hash.Add(value);
        return hash.ToHashCode();
    }
}

/// <summary>Rows where <c>Column &gt;= RangeMin AND Column &lt; RangeMax</c> — half-open, so
/// consecutive ranges tile a value space without gaps or overlaps.</summary>
public sealed record RangeSegment(string Column, string RangeMin, string RangeMax) : BatchReloadSegment
{
    public override string Describe() => $"{Column} [{RangeMin}, {RangeMax})";
}

/// <summary>
/// A request to split <paramref name="Column"/>'s actual value range into <paramref name="BucketCount"/>
/// <see cref="RangeSegment"/>s. Consumed exactly once, at whatever point segment expansion happens
/// (<see cref="ISegmentExpandingReader"/>) — an <c>Auto</c> segment is never persisted as, or handed to
/// a reader/writer as, a runtime segment.
/// </summary>
public sealed record AutoSegment(string Column, int BucketCount) : BatchReloadSegment
{
    public override string Describe() => $"{Column} auto/{BucketCount}";
}

/// <summary>
/// The one place <see cref="BatchReloadSegment"/> is serialized, so the polymorphic discriminator is
/// configured identically at every round-trip point: the ephemeral <c>options["segment"]</c> channel
/// (injected per work item) and the persisted YAML-embedded JSON array a standalone reload
/// replication uses for its static segment list.
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

    /// <summary>The well-known reader-options key carrying a standalone reload replication's
    /// persisted JSON array of segments.</summary>
    public const string SegmentsOptionKey = "segments";

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
