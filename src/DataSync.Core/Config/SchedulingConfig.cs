namespace DataSync.Core.Config;

public enum ScheduleMode
{
    Continuous,
    Periodic,
}

/// <summary>
/// Mirrors the two scheduling methods from architecture/planning/architecture.md: a continuous
/// loop with a frequency, or a periodic cron-style schedule. Exactly one of
/// <see cref="FrequencySeconds"/> / <see cref="CronExpression"/> applies, selected by <see cref="Mode"/>.
/// </summary>
public sealed class SchedulingConfig
{
    public required ScheduleMode Mode { get; set; }
    public int? FrequencySeconds { get; set; }
    public string? CronExpression { get; set; }
}
