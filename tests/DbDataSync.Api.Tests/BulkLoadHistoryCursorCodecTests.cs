using DbDataSync.Api.Services;
using DbDataSync.State;
using Xunit;

namespace DbDataSync.Api.Tests;

/// <summary>
/// The bulk-load-history cursor's opaque encoding, and its "was this issued under this filter" check —
/// see phase 139. Mirrors <see cref="RunHistoryCursorCodecTests"/>.
/// <para>
/// Pure unit tests, deliberately: nothing here touches <see cref="TestApiFactory"/> or an HTTP
/// pipeline, for the same reason that codec's own tests don't.
/// </para>
/// </summary>
public sealed class BulkLoadHistoryCursorCodecTests
{
    private static readonly BulkLoadHistoryCursor Cursor = new(
        DateTimeOffset.Parse("2026-05-01T09:20:00Z"), "abc123");

    [Fact]
    public void EncodeThenDecode_WithTheSameMappingFilter_RoundTrips()
    {
        var token = BulkLoadHistoryCursorCodec.Encode(Cursor, "crm-sync", "orders");

        var decoded = BulkLoadHistoryCursorCodec.Decode(token, "crm-sync", "orders");

        Assert.Equal(Cursor, decoded);
    }

    [Fact]
    public void EncodeThenDecode_WithNoMappingFilter_RoundTrips()
    {
        var token = BulkLoadHistoryCursorCodec.Encode(Cursor, "crm-sync", null);

        Assert.Equal(Cursor, BulkLoadHistoryCursorCodec.Decode(token, "crm-sync", null));
    }

    [Fact]
    public void Decode_NullOrEmpty_MeansPageOne()
    {
        Assert.Null(BulkLoadHistoryCursorCodec.Decode(null, "crm-sync", null));
        Assert.Null(BulkLoadHistoryCursorCodec.Decode("", "crm-sync", null));
    }

    [Fact]
    public void Decode_GarbageToken_ResetsToPageOne_RatherThanThrowing()
    {
        Assert.Null(BulkLoadHistoryCursorCodec.Decode("not-a-real-token!!", "crm-sync", null));
    }

    /// <summary>
    /// A cursor minted under one replication or mapping filter, replayed against a different one, is
    /// treated exactly like having no cursor at all — page one under the *new* filter — rather than a
    /// 400. Each assertion changes exactly one of the two things a cursor is scoped to.
    /// </summary>
    [Theory]
    [InlineData("other-sync", "orders")] // different replication
    [InlineData("crm-sync", "customers")] // different mapping (was orders)
    public void Decode_UnderADifferentFilter_ResetsToPageOne(string taskName, string? mappingName)
    {
        var token = BulkLoadHistoryCursorCodec.Encode(Cursor, "crm-sync", "orders");

        var decoded = BulkLoadHistoryCursorCodec.Decode(token, taskName, mappingName);

        Assert.Null(decoded);
    }
}
