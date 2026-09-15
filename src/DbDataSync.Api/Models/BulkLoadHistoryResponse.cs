using DbDataSync.State;

namespace DbDataSync.Api.Models;

/// <summary>
/// The bulk-load-history endpoint's shape since phase 139 — a page of batches, and where the next one
/// starts. Mirrors <see cref="RunHistoryResponse"/>. <see cref="NextCursor"/> is null once the page
/// reaches the end of the history, and never a cursor that would loop back to the first page.
/// </summary>
public sealed record BulkLoadHistoryResponse(IReadOnlyList<BulkLoadBatchProgress> Batches, string? NextCursor);
