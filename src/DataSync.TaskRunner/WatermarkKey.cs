using DataSync.Core.Config;

namespace DataSync.TaskRunner;

/// <summary>Stable identifier for a source table within a task, used as the "SourceTable" key in
/// DataSync.State's ChangeWatermarks table.</summary>
public static class WatermarkKey
{
    public static string Build(SourceTableRef source) =>
        $"{source.ConnectionName}/{source.Database}/{source.Schema}.{source.Table}";
}
