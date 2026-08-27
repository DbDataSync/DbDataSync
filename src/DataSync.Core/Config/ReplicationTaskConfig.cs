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

    /// <summary>
    /// The source and target this replication's table mappings read from and write to. A mapping
    /// inherits these unless it sets its own — see <see cref="TableSpec"/>.
    /// <para>
    /// Optional so that config written before endpoints existed still loads: those replications carry
    /// a full endpoint on every mapping and simply never consult this.
    /// </para>
    /// </summary>
    public TaskEndpoints Endpoints { get; set; } = new();
}
