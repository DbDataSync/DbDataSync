using DbDataSync.State;

namespace DbDataSync.Api.Models;

/// <summary>
/// The run-history endpoint's shape since phase 104 added paging — a page of runs, and where the next
/// one starts. <see cref="NextCursor"/> is null once the page reaches the end of the history, and
/// never a cursor that would loop back to the first page.
/// </summary>
public sealed record RunHistoryResponse(IReadOnlyList<TaskRunRecord> Runs, string? NextCursor);
