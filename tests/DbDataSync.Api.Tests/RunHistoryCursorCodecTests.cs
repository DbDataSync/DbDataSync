using DbDataSync.Api.Services;
using DbDataSync.State;
using Xunit;

namespace DbDataSync.Api.Tests;

/// <summary>
/// The run-history cursor's opaque encoding, and the "was this issued under these filters" check that
/// rides along inside it — see phase 104.
/// <para>
/// Pure unit tests, deliberately: nothing here touches <see cref="TestApiFactory"/> or an HTTP
/// pipeline, so these run — and prove something — in an environment where the TestServer/Negotiate
/// incompatibility documented on <c>RunsControllerTests</c> would otherwise swallow every assertion
/// behind a 500.
/// </para>
/// </summary>
public sealed class RunHistoryCursorCodecTests
{
    private static readonly RunHistoryCursor Cursor = new(
        DateTimeOffset.Parse("2026-05-01T09:20:00Z"), Guid.Parse("11111111-1111-1111-1111-111111111111"));

    [Fact]
    public void EncodeThenDecode_WithTheSameFilters_RoundTrips()
    {
        var token = RunHistoryCursorCodec.Encode(Cursor, "crm-sync", RunKind.BulkLoad, "orders", RunStatus.Failed);

        var decoded = RunHistoryCursorCodec.Decode(
            token, "crm-sync", RunKind.BulkLoad, "orders", RunStatus.Failed);

        Assert.Equal(Cursor, decoded);
    }

    [Fact]
    public void EncodeThenDecode_WithNoFiltersAtAll_RoundTrips()
    {
        var token = RunHistoryCursorCodec.Encode(Cursor, "crm-sync", null, null, null);

        Assert.Equal(Cursor, RunHistoryCursorCodec.Decode(token, "crm-sync", null, null, null));
    }

    [Fact]
    public void Decode_NullOrEmpty_MeansPageOne()
    {
        Assert.Null(RunHistoryCursorCodec.Decode(null, "crm-sync", null, null, null));
        Assert.Null(RunHistoryCursorCodec.Decode("", "crm-sync", null, null, null));
    }

    [Fact]
    public void Decode_GarbageToken_ResetsToPageOne_RatherThanThrowing()
    {
        Assert.Null(RunHistoryCursorCodec.Decode("not-a-real-token!!", "crm-sync", null, null, null));
    }

    /// <summary>
    /// The decision the phase doc leaves open, resolved: a cursor minted under one set of filters,
    /// replayed against a different one, is treated exactly like having no cursor at all — page one
    /// under the *new* filters — rather than a 400. Each assertion changes exactly one of the four
    /// things a cursor is scoped to.
    /// </summary>
    [Theory]
    [InlineData("other-sync", "BulkLoad", "orders", "Failed")] // different replication
    [InlineData("crm-sync", "Primary", "orders", "Failed")] // different kind (was BulkLoad)
    [InlineData("crm-sync", "BulkLoad", "customers", "Failed")] // different mapping (was orders)
    [InlineData("crm-sync", "BulkLoad", "orders", "Succeeded")] // different status (was Failed)
    public void Decode_UnderDifferentFilters_ResetsToPageOne(
        string taskName, string? kind, string? mappingName, string? status)
    {
        var token = RunHistoryCursorCodec.Encode(Cursor, "crm-sync", RunKind.BulkLoad, "orders", RunStatus.Failed);

        var decoded = RunHistoryCursorCodec.Decode(
            token, taskName,
            kind is null ? null : Enum.Parse<RunKind>(kind),
            mappingName,
            status is null ? null : Enum.Parse<RunStatus>(status));

        Assert.Null(decoded);
    }
}
