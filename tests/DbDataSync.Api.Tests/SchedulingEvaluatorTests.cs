using DbDataSync.Api.Services;
using DbDataSync.Core.Config;
using Xunit;

namespace DbDataSync.Api.Tests;

public sealed class SchedulingEvaluatorTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 25, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void IsDue_WhenNeverRun_IsDueImmediately()
    {
        var scheduling = new SchedulingConfig { Mode = ScheduleMode.Continuous, FrequencySeconds = 3600 };

        Assert.True(SchedulingEvaluator.IsDue(scheduling, lastRunStartedAtUtc: null, Now));
    }

    [Theory]
    [InlineData(30, 29, false)]
    [InlineData(30, 30, true)]
    [InlineData(30, 31, true)]
    public void IsDue_Continuous_ComparesElapsedSecondsAgainstFrequency(int frequencySeconds, int elapsedSeconds, bool expectedDue)
    {
        var scheduling = new SchedulingConfig { Mode = ScheduleMode.Continuous, FrequencySeconds = frequencySeconds };
        var lastRun = Now.AddSeconds(-elapsedSeconds);

        Assert.Equal(expectedDue, SchedulingEvaluator.IsDue(scheduling, lastRun, Now));
    }

    [Fact]
    public void IsDue_Periodic_WithoutCronExpression_IsNeverDue()
    {
        var scheduling = new SchedulingConfig { Mode = ScheduleMode.Periodic, CronExpression = null };

        Assert.False(SchedulingEvaluator.IsDue(scheduling, Now.AddDays(-1), Now));
    }

    [Fact]
    public void IsDue_Periodic_WhenNextOccurrenceIsInThePast_IsDue()
    {
        // Every day at 00:00 UTC; last run was two days ago, so today's midnight has already passed.
        var scheduling = new SchedulingConfig { Mode = ScheduleMode.Periodic, CronExpression = "0 0 * * *" };
        var lastRun = Now.AddDays(-2);

        Assert.True(SchedulingEvaluator.IsDue(scheduling, lastRun, Now));
    }

    [Fact]
    public void IsDue_Periodic_WhenNextOccurrenceIsInTheFuture_IsNotDue()
    {
        // Last run was at "now" itself; the next daily occurrence is tomorrow, not yet due.
        var scheduling = new SchedulingConfig { Mode = ScheduleMode.Periodic, CronExpression = "0 0 * * *" };

        Assert.False(SchedulingEvaluator.IsDue(scheduling, Now, Now));
    }
}
