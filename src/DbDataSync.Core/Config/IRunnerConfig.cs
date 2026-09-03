using DbDataSync.Core.Git;

namespace DbDataSync.Core.Config;

/// <summary>
/// The config writes a TaskRunner performs — the whole boundary between a run and the config
/// repository, and the config-side sibling of <c>DbDataSync.State.IRunnerState</c>.
/// <para>
/// **A sibling interface rather than another method on <c>IRunnerState</c>.** That interface is
/// explicitly about the state store: its doc comment says so, its two halves (prerequisites and
/// outcomes) are what make the offline journal expressible, and every one of its members lands in a
/// SQLite table. Config is a different owned resource with a different owner — <see cref="ConfigRepository"/>,
/// git-backed, validating, and producing a commit per write. Putting a git commit behind a name that
/// says "state" would be the kind of thing that reads correctly once and misleads every time after.
/// </para>
/// <para>
/// It travels over the *same* loopback channel, though — same host, same token, same route prefix (see
/// <c>RunnerConfigEndpoints</c>). Two interfaces, one connection: what is being written is a different
/// question from how a child process reaches its parent.
/// </para>
/// <para>
/// The API keeps direct <see cref="ConfigRepository"/> access; it *is* the owning process, and until
/// this existed every config write in the product originated from one of its controllers. This exists
/// for the runners it spawns — see phase 94.
/// </para>
/// </summary>
public interface IRunnerConfig
{
    /// <summary>
    /// Records the target table's shape as it stands now that this run has provisioned it, into the
    /// mapping's phase-90 column cache.
    /// <para>
    /// **Best-effort, and deliberately not journalled.** Unlike a watermark — whose whole point is
    /// that the position it names can never be recomputed once the rows are written — the condition
    /// that produces this report recurs: an auto-provisioning pass that finds a target whose shape is
    /// not cached reports it again next time round (see <c>RunExecutor</c>'s provisioning step). A lost
    /// report therefore costs one more pass, not an operator's manual Refresh, which is what makes
    /// spilling it to disk unnecessary rather than merely inconvenient. An implementation that cannot
    /// deliver it says so and returns; it never fails the run that had already done the real work.
    /// </para>
    /// </summary>
    /// <param name="columns">The target's columns as its own catalog describes them — not the
    /// provisioning plan's requested shape. The cache's contract is "what somebody saw when they last
    /// looked", and only the catalog can say what a rendered type became or which column ended up an
    /// identity.</param>
    void ReportProvisionedTargetColumns(
        string replicationName, string mappingName, IReadOnlyList<CachedColumn> columns);
}

/// <summary>
/// <see cref="IRunnerConfig"/> against the config repository directly, for the process that owns it.
/// <para>
/// Used by the API — which is the owner — and by tests, which want the real behaviour without a
/// loopback server in the way. The <see cref="ConfigRepository"/>/<see cref="GitAuthor"/> pair is the
/// same one every controller already writes through, so an automated report is validated and committed
/// on exactly the terms an operator's edit is. Mirrors <c>LocalRunnerState</c>.
/// </para>
/// </summary>
/// <param name="author">Who these commits are attributed to. The API passes
/// <c>CurrentUser.SystemAuthor</c> — the identity that doc comment already reserves for "a background
/// service writing config on its own" — because there is no operator on this path and attributing a
/// commit to whoever last signed in would put a person's name on a write they did not make.</param>
public sealed class LocalRunnerConfig(ConfigRepository configRepository, GitAuthor author) : IRunnerConfig
{
    public void ReportProvisionedTargetColumns(
        string replicationName, string mappingName, IReadOnlyList<CachedColumn> columns)
    {
        // Re-loaded rather than taking the runner's copy. The runner's mapping is as old as the start
        // of its pass, and writing it back whole would revert any edit an operator made in between —
        // this write is about one field, so it is applied to one field of the current file.
        var mapping = configRepository.LoadTableMapping(replicationName, mappingName);

        // Nothing to say, so nothing committed. This is what keeps the report free to fire on every
        // provisioning pass rather than only the one that ran DDL: a repeat report on a cache that
        // already agrees produces no commit at all, so the recurrence that makes journalling
        // unnecessary does not cost a commit per pass to buy.
        if (Matches(mapping.TargetColumns, columns))
            return;

        mapping.TargetColumns = [.. columns];
        // Stamped for the same reason a Refresh stamps it: a side really was read just now, and the
        // age on screen is what an operator decides staleness from.
        mapping.ColumnsCapturedUtc = DateTime.UtcNow;

        configRepository.SaveTableMapping(replicationName, mapping, author);
    }

    /// <summary>The same equality <c>MappingMetadataCapture</c> decides "this save changed nothing"
    /// by, and order-sensitive for the same reason: a catalog's column order is part of what was
    /// captured.</summary>
    private static bool Matches(List<CachedColumn> stored, IReadOnlyList<CachedColumn> incoming) =>
        stored.Count == incoming.Count
        && stored.Zip(incoming).All(pair =>
            string.Equals(pair.First.Name, pair.Second.Name, StringComparison.Ordinal)
            && pair.First.SameShapeAs(pair.Second));
}
