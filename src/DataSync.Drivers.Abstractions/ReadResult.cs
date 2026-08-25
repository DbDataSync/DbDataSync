namespace DataSync.Drivers.Abstractions;

/// <summary>
/// <paramref name="NewCursor"/> is computed by the reader up front (e.g. the Change Tracking version
/// to read up to) before <paramref name="Rows"/> is enumerated, not derived from what was actually
/// read — so it's always safe for the caller to persist it as the new watermark once every row in
/// <paramref name="Rows"/> has been successfully staged and applied.
/// </summary>
public sealed record ReadResult(IAsyncEnumerable<ChangeRow> Rows, string NewCursor);
