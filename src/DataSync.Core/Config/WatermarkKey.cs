namespace DataSync.Core.Config;

/// <summary>
/// Stable identifier for a source table within a replication, used as the "SourceTable" key in
/// DataSync.State's ChangeWatermarks table.
/// <para>
/// In Core rather than in the TaskRunner because the runner is no longer the only thing that needs
/// it: the preview (phase 37) reads the stored watermark so that what it describes is the statement
/// the *next* pass would issue. Two spellings of this key would be two answers to "where did this
/// replication get to", which is the one question it exists to answer.
/// </para>
/// </summary>
public static class WatermarkKey
{
    /// <summary>
    /// Two source tables collide here if one's schema ends where the other's table begins — a schema
    /// literally named <c>a.b</c> with table <c>c</c> keys the same as schema <c>a</c> with table
    /// <c>b.c</c>. Both are legal quoted identifiers.
    /// <para>
    /// Left as it is, deliberately. Changing the format orphans every watermark already stored, which
    /// means a silent full resync of every replication on upgrade — a certain, universal cost against
    /// a collision that needs two dotted identifiers arranged to overlap. Worth fixing behind a
    /// migration if anything ever makes it more than theoretical; not worth a resync today.
    /// </para>
    /// </summary>
    public static string Build(SourceTableRef source) =>
        $"{source.ConnectionName}/{source.Database}/{source.Schema}.{source.Table}";
}
