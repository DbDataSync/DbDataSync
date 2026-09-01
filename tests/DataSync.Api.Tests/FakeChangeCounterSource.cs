using DataSync.Api.Services;

namespace DataSync.Api.Tests;

/// <summary>
/// A source that answers from dictionaries and remembers being asked.
/// <para>
/// Recording the calls is the point of half the gate's tests — "was this fetched once or twice" is
/// the optimization itself, and it is invisible in the gate's return value — and of the lag tests
/// that assert a figure came out of stored history rather than out of a round-trip nobody wanted a
/// status screen to make.
/// </para>
/// <para>
/// Shared by <see cref="ChangePollingGateTests"/> and <see cref="ReaderLagTests"/> because they fake
/// the same one thing for the same reason: it is the only part of either that talks to a source.
/// Everything else both exercise — config, the driver registry, the two state tables — is the real
/// thing the API is wired with.
/// </para>
/// </summary>
public sealed class FakeChangeCounterSource : IChangeCounterSource
{
    public Dictionary<(string Connection, string Database, string Kind), ChangeCounterReading> Values { get; } = [];

    /// <summary>
    /// What the engine's own mapping would say about a given stored position —
    /// <c>fn_cdc_map_lsn_to_time</c> for an LSN, <c>dm_tran_commit_table</c> for a Change Tracking
    /// version. Keyed by the stored text of the position, as both are.
    /// <para>
    /// **A position with no entry maps to null, which is the whole point of the dictionary being
    /// sparse**: it is how both mechanisms answer for a position that has aged out of what they
    /// retain, and for Change Tracking it is what makes the estimate the answer instead. A test
    /// exercising the fallback path says so by leaving the version out of here.
    /// </para>
    /// </summary>
    public Dictionary<string, DateTimeOffset> Times { get; } = [];

    public HashSet<(string Connection, string Database, string Kind)> Unreachable { get; } = [];

    public List<(string Connection, string Database, string Kind)> Fetches { get; } = [];

    public List<string> Mappings { get; } = [];

    public void Set(string connection, string database, string kind, string? value,
        DateTimeOffset? sourceTime = null) =>
        Values[(connection, database, kind)] = new ChangeCounterReading(value, sourceTime);

    public Task<ChangeCounterReading> FetchAsync(
        string connectionName, string sourceDatabase, string readerKind, CancellationToken cancellationToken)
    {
        var key = (connectionName, sourceDatabase, readerKind);
        Fetches.Add(key);

        if (Unreachable.Contains(key))
            throw new InvalidOperationException("the source is not answering");

        return Task.FromResult(
            Values.TryGetValue(key, out var reading) ? reading : new ChangeCounterReading(null));
    }

    public Task<DateTimeOffset?> MapSourceTimeAsync(
        string connectionName,
        string sourceDatabase,
        string readerKind,
        string value,
        CancellationToken cancellationToken)
    {
        if (Unreachable.Contains((connectionName, sourceDatabase, readerKind)))
            throw new InvalidOperationException("the source is not answering");

        Mappings.Add(value);
        return Task.FromResult<DateTimeOffset?>(Times.TryGetValue(value, out var time) ? time : null);
    }
}
