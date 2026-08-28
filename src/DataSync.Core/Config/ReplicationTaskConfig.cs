using System.ComponentModel;

namespace DataSync.Core.Config;

/// <summary>
/// Persisted as config/replications/&lt;name&gt;/task.yaml. Table mappings for this replication are
/// separate files under table-mappings/ (see <see cref="TableMappingConfig"/>), not embedded here.
/// </summary>
public sealed class ReplicationTaskConfig
{
    public required string Name { get; set; }
    /// <summary>
    /// Whether the scheduler runs this replication at all.
    /// <para>
    /// <see cref="DefaultValueAttribute"/> is load-bearing, not documentation. The YAML serializer is
    /// configured to omit defaults, and it compares against <c>default(T)</c> unless told otherwise —
    /// so <c>false</c>, being <c>default(bool)</c>, was never written, and this property's own
    /// initializer set it straight back to <c>true</c> on load. Disabling a replication did nothing.
    /// With the attribute the comparison is against <c>true</c>, so <c>false</c> is written and
    /// <c>true</c> is omitted, which is the right way round.
    /// </para>
    /// </summary>
    [DefaultValue(true)]
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

    /// <summary>Scripts bound at this level, keyed by slot (see <c>ScriptSlots</c>). An absent key
    /// inherits from a broader level; a key present with a null value is "explicitly none" and
    /// overrides an inherited binding. See <see cref="ScriptResolution"/>.</summary>
    public Dictionary<string, ScriptBinding?> Scripts { get; set; } = new();

    /// <summary>Hooks bound at this level, keyed by point (see <c>HookPoints</c>). See
    /// <see cref="HookResolution"/>.</summary>
    public Dictionary<string, List<HookConfig>?> Hooks { get; set; } = new();
}
