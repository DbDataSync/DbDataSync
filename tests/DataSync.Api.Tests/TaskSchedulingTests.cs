using DataSync.Api.Services;

namespace DataSync.Api.Tests;

/// <summary>The two gates, and the one definition that combines them — phase 64.</summary>
public sealed class TaskSchedulingTests
{
    [Theory]
    [InlineData(true, false, true)]   // enabled, not held — the only case that runs
    [InlineData(true, true, false)]   // paused overrides an enabled replication
    [InlineData(false, false, false)] // disabled, whatever the pause says
    [InlineData(false, true, false)]
    public void ShouldRun_RequiresEnabledAndNotPaused(bool enabled, bool paused, bool expected) =>
        Assert.Equal(expected, TaskScheduling.ShouldRun(enabled, paused));
}
