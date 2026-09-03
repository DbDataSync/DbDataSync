using System.ComponentModel;

namespace DbDataSync.Core.Config;

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

    /// <summary>
    /// What every table mapping under this replication may do to its target's shape, unless the
    /// mapping says otherwise. Same type as the mapping's own, resolved by
    /// <see cref="ProvisioningResolution"/> — one answer set in one place beats the same checkbox
    /// ticked on forty mappings.
    /// </summary>
    public ProvisioningConfig Provisioning { get; set; } = new();

    /// <summary>
    /// Named ways of dividing a table for reload, referenced by name from any of this replication's
    /// mappings — see phase 58 and <c>CustomSegment</c>.
    /// <para>
    /// At the replication rather than on each mapping because that is the scope at which one is worth
    /// reusing: a set of tables replicated together usually segments by the same convention, and
    /// "reload by calendar month" written once beats it written on forty mappings. A mapping still
    /// chooses whether to use one.
    /// </para>
    /// </summary>
    public List<SegmentingStrategyConfig> SegmentingStrategies { get; set; } = new();

    /// <summary>
    /// Whatever whoever owns this replication needs the next person to know. Markdown, git-tracked and
    /// diffed like every other field here — see phase 64.
    /// <para>
    /// Config rather than state, unlike the pause note beside it, and the difference is deliberate: a
    /// pause note is about one incident and is worth nothing a month later, while this is about the
    /// replication itself and its edits are exactly the kind of thing the history view is for.
    /// </para>
    /// </summary>
    public string? Notes { get; set; }
}
