namespace DbDataSync.Drivers.Abstractions;

/// <summary>
/// The source discarded the history this reader needed: the stored position is older than the oldest
/// the mechanism still retains.
/// <para>
/// Every log-based mechanism keeps history for a window, so every one of them has this failure — SQL
/// Server Change Tracking below its minimum valid version, CDC below its minimum LSN, a Postgres slot
/// whose WAL has been recycled. Before this existed there was one bespoke <c>throw</c> in the Change
/// Tracking reader with its own wording, and a second mechanism would have meant a second one; two
/// messages for one situation is how a support burden starts.
/// </para>
/// <para>
/// It is a distinct type rather than a message because the **fix is distinct**. A generic failure
/// means "look at the logs"; this one means "the incremental position is unusable and the table has
/// to be reloaded", which is a specific action the product can offer rather than describe.
/// </para>
/// </summary>
/// <param name="tableName">Qualified, as an operator would write it.</param>
/// <param name="storedPosition">The watermark that can no longer be served.</param>
/// <param name="oldestAvailable">The oldest position the source still holds.</param>
/// <param name="mechanism">Which change mechanism said so, for a message that names the thing to go
/// and look at.</param>
public sealed class PositionExpiredException(
    string tableName, string storedPosition, string oldestAvailable, string mechanism)
    : Exception(
        $"{mechanism} history for '{tableName}' no longer covers position '{storedPosition}' " +
        $"(the oldest still available is '{oldestAvailable}'). The changes in between are gone, so " +
        "this table has to be reloaded — its stored watermark cannot be caught up.")
{
    public string TableName { get; } = tableName;
    public string StoredPosition { get; } = storedPosition;
    public string OldestAvailable { get; } = oldestAvailable;
    public string Mechanism { get; } = mechanism;
}
