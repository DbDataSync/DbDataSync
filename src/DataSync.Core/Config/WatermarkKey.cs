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
    public static string Build(SourceTableRef source) =>
        $"{source.ConnectionName}/{source.Database}/{source.Schema}.{source.Table}";
}
