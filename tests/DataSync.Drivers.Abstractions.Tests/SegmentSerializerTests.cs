using DataSync.Core.Config;
using System.Text.Json;
using DataSync.Drivers.Abstractions;
using Xunit;

namespace DataSync.Drivers.Abstractions.Tests;

/// <summary>
/// Segments cross two round-trip boundaries — the per-work-item options channel and a standalone
/// reload replication's persisted segment list — so a discriminator that silently doesn't get written
/// surfaces as a deserialization failure a long way from the code that caused it. These tests pin the
/// wire shape, not just that round-tripping happens to work.
/// </summary>
public sealed class SegmentSerializerTests
{
    [Fact]
    public void Serialize_EmitsModeDiscriminator()
    {
        var json = SegmentSerializer.Serialize(new RangeSegment("OrderId", "1", "100"));

        using var document = JsonDocument.Parse(json);
        Assert.Equal("range", document.RootElement.GetProperty("mode").GetString());
    }

    /// <summary>
    /// The footgun this guards: System.Text.Json only writes the discriminator when the *static* type
    /// being serialized is the polymorphic base. Handing it a variable typed as the concrete derived
    /// record produces discriminator-free JSON that fails on the way back in. SegmentSerializer's
    /// signatures take the base type so this can't happen at a call site — proven here by passing a
    /// concretely-typed local, which is exactly how a caller would get it wrong.
    /// </summary>
    [Fact]
    public void Serialize_FromConcretelyTypedLocal_StillEmitsDiscriminator()
    {
        FullSegment concrete = new();

        var json = SegmentSerializer.Serialize(concrete);

        Assert.Contains("\"mode\":\"full\"", json);
        Assert.IsType<FullSegment>(SegmentSerializer.Deserialize(json));
    }

    [Theory]
    [MemberData(nameof(EverySegmentMode))]
    public void RoundTrip_PreservesModeAndFields(BatchReloadSegment segment)
    {
        var result = SegmentSerializer.Deserialize(SegmentSerializer.Serialize(segment));

        Assert.Equal(segment, result);
    }

    public static TheoryData<BatchReloadSegment> EverySegmentMode() =>
    [
        new FullSegment(),
        new ListSegment("Region", ["EU", "US"]),
        new RangeSegment("OrderDate", "2024-01-01T00:00:00.0000000", "2024-02-01T00:00:00.0000000"),
        new RangeSegment("OrderDate", "2024-03-01", "2024-04-01", "2024-03"),
        new AutoSegment("OrderId", 8),
    ];

    [Fact]
    public void SerializeMany_RoundTripsAMixedArray()
    {
        IReadOnlyList<BatchReloadSegment> segments =
        [
            new FullSegment(),
            new ListSegment("Region", ["EU"]),
            new RangeSegment("OrderId", "1", "100"),
        ];

        var result = SegmentSerializer.DeserializeMany(SegmentSerializer.SerializeMany(segments));

        Assert.Equal(segments, result);
    }

    [Fact]
    public void ReadOptional_ReturnsNullWhenTheWorkItemCarriesNoSegment()
    {
        Assert.Null(SegmentSerializer.ReadOptional(new Dictionary<string, string>()));
        Assert.Null(SegmentSerializer.ReadOptional(
            new Dictionary<string, string> { [SegmentSerializer.SegmentOptionKey] = "  " }));
    }

    [Fact]
    public void ReadOptional_ReadsTheSegmentInjectedIntoOptions()
    {
        var options = new Dictionary<string, string>
        {
            [SegmentSerializer.SegmentOptionKey] = SegmentSerializer.Serialize(new ListSegment("Region", ["EU"])),
        };

        Assert.Equal(new ListSegment("Region", ["EU"]), SegmentSerializer.ReadOptional(options));
    }

    [Fact]
    public void Describe_IsReadableEnoughToUseAsARunLabel()
    {
        Assert.Equal("full", new FullSegment().Describe());
        Assert.Equal("Region in (EU,US)", new ListSegment("Region", ["EU", "US"]).Describe());
        Assert.Equal("OrderId [1, 100)", new RangeSegment("OrderId", "1", "100").Describe());
        Assert.Equal("OrderId auto/8", new AutoSegment("OrderId", 8).Describe());
    }
}
