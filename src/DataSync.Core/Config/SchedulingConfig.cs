namespace DataSync.Core.Config;

public enum ScheduleMode
{
    Continuous,
    Periodic,
}

/// <summary>
/// Mirrors the two scheduling methods from architecture/planning/done/architecture.md: a continuous
/// loop with a frequency, or a periodic cron-style schedule. Exactly one of
/// <see cref="FrequencySeconds"/> / <see cref="CronExpression"/> applies, selected by <see cref="Mode"/>.
/// </summary>
public sealed class SchedulingConfig
{
    /// <summary>How long a continuous worker keeps looking and finding nothing before it gives up and
    /// exits, when the config does not say. See <see cref="IdleTimeoutSeconds"/>.</summary>
    public const int DefaultIdleTimeoutSeconds = 60;

    public required ScheduleMode Mode { get; set; }
    public int? FrequencySeconds { get; set; }
    public string? CronExpression { get; set; }

    /// <summary>
    /// How long a continuous worker goes without finding a single changed row before it exits, in
    /// seconds. Null means <see cref="DefaultIdleTimeoutSeconds"/>.
    /// <para>
    /// A worker exiting when there is nothing to do is deliberate — it is what makes an idle
    /// replication cost nothing between passes. What was wrong is how fast it happened: the worker
    /// exited as soon as the *queue* was empty, which is true for a fraction of a second after every
    /// single pass. Under a live load that meant spawn, one pass, exit, respawn, several times a
    /// minute, and the process churn was larger than the work.
    /// </para>
    /// <para>
    /// Nullable rather than an <c>int</c> with an initializer, so the serializer's omit-defaults
    /// handling cannot swallow a deliberate value: <c>default(int?)</c> is null, which is exactly what
    /// "nobody said" means here. Same trap as <c>ReplicationTaskConfig.Enabled</c>, avoided by shape
    /// rather than by attribute.
    /// </para>
    /// </summary>
    public int? IdleTimeoutSeconds { get; set; }

    /// <summary>The resolved idle timeout — what a worker actually uses.</summary>
    [YamlDotNet.Serialization.YamlIgnore]
    [System.Text.Json.Serialization.JsonIgnore]
    public TimeSpan IdleTimeout => TimeSpan.FromSeconds(IdleTimeoutSeconds ?? DefaultIdleTimeoutSeconds);

    /// <summary>
    /// How long a continuous worker waits between passes when it has nothing to do. Falls back to the
    /// idle timeout rather than to zero: a frequency that is unset means nobody chose a cadence, and
    /// spinning is a worse guess than waiting.
    /// </summary>
    [YamlDotNet.Serialization.YamlIgnore]
    [System.Text.Json.Serialization.JsonIgnore]
    public TimeSpan Frequency => TimeSpan.FromSeconds(FrequencySeconds ?? IdleTimeoutSeconds ?? DefaultIdleTimeoutSeconds);
}
