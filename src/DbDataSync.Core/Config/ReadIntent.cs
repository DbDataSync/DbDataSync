namespace DbDataSync.Core.Config;

/// <summary>
/// What a mapping's next pass is meant to do — a request the reader interprets as best it can, not an
/// observation of how things are. Named "intent" rather than "state" deliberately, for exactly the
/// reason <c>ProvisioningState</c> is an observation and must not read like this: a config setting or an
/// operator can only ever ask for a pass, never assert that one already happened.
/// <para>
/// See architecture/planning/done/reset-a-mappings-watermark-from-the-ui.md for the design this
/// implements, architecture/implementation/done/phase-100-read-intent-storage-and-defaults.md for what
/// phase 100 built from it, and
/// architecture/implementation/done/phase-101-readers-honour-the-read-intent.md for what reads it:
/// <c>RunExecutor</c> resolves this (stored value, else <see cref="ReadIntentResolution.Default"/>) and
/// hands it to the reader instead of inferring a full load from a null watermark.
/// </para>
/// </summary>
public enum ReadIntent
{
    /// <summary>Read the source table itself. Today's null-watermark behaviour, now stated rather than
    /// inferred. Per
    /// architecture/planning/done/bulk-load-pipeline-and-the-initial-load-rule.md, once a Bulk Load
    /// pipeline exists (future, unscheduled work) this stops meaning "the reader will full-load" and
    /// starts meaning "run the Bulk Load pipeline" — the same intent, performed somewhere else. Until
    /// then, every reader's own <c>previousWatermark is null</c> full-load branch is what this still
    /// triggers.</summary>
    InitialLoad,

    /// <summary>Ordinary incremental read from the stored position. The steady state every mapping that
    /// has ever completed a pass is in.</summary>
    Changes,

    /// <summary>Read everything the feed still holds — the earliest position the source can still
    /// answer for — without a full load. Transitions to <see cref="Changes"/> once its pass applies.
    /// Not every reader can honour this; the generic Watermark reader cannot, because for it the feed
    /// *is* the table and this would be a full load under another name.</summary>
    ChangesFromEarliest,

    /// <summary>Skip to now: adopt the current position without reading anything that came before it.
    /// Transitions to <see cref="Changes"/> once its pass applies.</summary>
    ChangesFromLatest,
}

/// <summary>
/// Why a mapping's next <c>Primary</c> pass is not going to run — a reason, never a fifth
/// <see cref="ReadIntent"/>. The two answer different questions and collapsing them loses information at
/// exactly the moments it matters: a mapping paused mid-<see cref="ReadIntent.ChangesFromEarliest"/> must
/// resume to that same intent, not to <see cref="ReadIntent.Changes"/>, and recovering from a hold sets
/// an intent and clears the hold as one act — two facts about the same row that a single column cannot
/// hold while the decision is being made. See the plan doc's "A second column, not a fifth intent".
/// </summary>
public enum ReadHold
{
    /// <summary>Nothing is holding this mapping back.</summary>
    None,

    /// <summary>The source discarded the history a pass needed — see <c>PositionExpiredException</c>.
    /// Not set by phase 100; phase 101 is what raises this on a failed pass, in place of retrying forever.</summary>
    PositionExpired,

    /// <summary>An operator stopped this one table specifically, at the finer of the two pause grains —
    /// beside phase 64's per-replication <c>Tasks.Paused</c>, not instead of it.</summary>
    Paused,
}

/// <summary>
/// What a mapping's read intent resolves to when nothing has ever been stored for it — a mapping that
/// has never completed a pass has no <c>ChangeWatermarks</c> row at all, so something has to say what
/// that absence means. Mirrors <see cref="ProvisioningResolution"/>: each level falls back
/// independently, most specific first, and the application default is what today's behaviour becomes
/// once it is stated rather than inferred.
/// <para>
/// **Only for a mapping with no stored row.** Once a mapping has run, its own stored intent (see
/// <c>ChangeWatermarkStore.GetReadState</c>) is the answer and this resolution never runs again for
/// it — the same relationship <see cref="ProvisioningResolution"/> has no stored counterpart to defer
/// to, because provisioning has no per-pass state of its own the way a watermark row does.
/// </para>
/// </summary>
public static class ReadIntentResolution
{
    public static ReadIntent Default(ReplicationTaskConfig? task, TableMappingConfig? mapping) =>
        mapping?.DefaultReadIntent ?? task?.DefaultReadIntent ?? ReadIntent.InitialLoad;

    /// <summary>Where the resolved default came from, for a UI that wants to show INHERITED.</summary>
    public static BindingLevel LevelOf(TableMappingConfig? mapping) =>
        mapping?.DefaultReadIntent is null ? BindingLevel.Replication : BindingLevel.Mapping;
}
