namespace DataSync.Drivers.Generic;

/// <summary>
/// The bookkeeping columns the snapshot and SCD Type 2 writers add beyond the mapped ones.
/// <para>
/// Named here rather than in each writer, because phase 54's "compare current only" reads
/// <see cref="IsCurrent"/> and a mapping's provisioning writes it — three places agreeing about a
/// string is three places that can disagree.
/// </para>
/// <para>
/// The <c>DS_</c> prefix, like the shadow table's, so a bookkeeping column cannot collide with one
/// somebody is replicating. <c>ValidFrom</c>/<c>ValidTo</c> rather than <c>EffectiveFrom</c>/
/// <c>EffectiveTo</c>: it is the spelling in the dimensional-modelling literature this feature is
/// named after, so it is the one a person looking at the table will expect.
/// </para>
/// </summary>
public static class HistorizedColumns
{
    /// <summary>When a snapshot ran. Every row of one carries the same value, which is what makes
    /// "the most recent snapshot" a filter rather than a guess.</summary>
    public const string SnapshotAt = "DS_SnapshotAt";

    /// <summary>
    /// The SCD2 surrogate key, and the generated table's real primary key — the source's own key
    /// stops being unique in a target that keeps every version of it.
    /// </summary>
    public const string SurrogateKey = "DS_VersionKey";

    public const string ValidFrom = "DS_ValidFrom";

    /// <summary>Null while a version is the current one, which is what makes an open version findable
    /// without reading <see cref="IsCurrent"/> and the two answers unable to disagree.</summary>
    public const string ValidTo = "DS_ValidTo";

    /// <summary>
    /// Redundant with <c>ValidTo IS NULL</c>, and worth it: "the current row" is the query every
    /// consumer of an SCD2 table writes, and an indexable flag is what makes it cheap. Written
    /// together, in one statement, so they cannot drift.
    /// </summary>
    public const string IsCurrent = "DS_IsCurrent";
}
