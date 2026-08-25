namespace DataSync.Core.Config;

/// <summary>
/// Persisted as config/replications/&lt;name&gt;/task.yaml. Table mappings for this replication are
/// separate files under table-mappings/ (see <see cref="TableMappingConfig"/>), not embedded here.
/// </summary>
public sealed class ReplicationTaskConfig
{
    public required string Name { get; set; }
    public bool Enabled { get; set; } = true;
    public required SchedulingConfig Scheduling { get; set; }
    public required ChangeProcessingConfig ChangeProcessing { get; set; }
}
