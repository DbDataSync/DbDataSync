using Cronos;
using DataSync.Core.Config;

namespace DataSync.Api.Services;

/// <summary>Pure due-ness calculation, kept separate from SchedulerService's polling loop so it's
/// directly unit-testable without a background service or real clock.</summary>
public static class SchedulingEvaluator
{
    public static bool IsDue(SchedulingConfig scheduling, DateTimeOffset? lastRunStartedAtUtc, DateTimeOffset nowUtc)
    {
        if (lastRunStartedAtUtc is null)
            return true; // never run before — due immediately

        return scheduling.Mode switch
        {
            ScheduleMode.Continuous => IsContinuousDue(scheduling, lastRunStartedAtUtc.Value, nowUtc),
            ScheduleMode.Periodic => IsPeriodicDue(scheduling, lastRunStartedAtUtc.Value, nowUtc),
            _ => false,
        };
    }

    private static bool IsContinuousDue(SchedulingConfig scheduling, DateTimeOffset lastRunStartedAtUtc, DateTimeOffset nowUtc) =>
        nowUtc >= lastRunStartedAtUtc.AddSeconds(scheduling.FrequencySeconds ?? 0);

    private static bool IsPeriodicDue(SchedulingConfig scheduling, DateTimeOffset lastRunStartedAtUtc, DateTimeOffset nowUtc)
    {
        if (string.IsNullOrWhiteSpace(scheduling.CronExpression))
            return false;

        var expression = CronExpression.Parse(scheduling.CronExpression);
        var next = expression.GetNextOccurrence(lastRunStartedAtUtc.UtcDateTime, TimeZoneInfo.Utc);
        return next is not null && next.Value <= nowUtc.UtcDateTime;
    }
}
