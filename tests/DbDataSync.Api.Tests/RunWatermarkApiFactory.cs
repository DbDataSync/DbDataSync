using DbDataSync.Api.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace DbDataSync.Api.Tests;

/// <summary>
/// <see cref="TestApiFactory"/> with <see cref="SchedulerService"/>'s own hosted-service tick removed.
/// Only <see cref="RunWatermarkTimeTests"/> uses this: that class exercises how a page of run history is
/// dated and has no use for a live scheduler enqueueing Primary passes underneath it. A live one is
/// exactly what made <c>EveryRunOnThePageIsDatedFromOneReadOfTheGroupsHistory</c> claim the wrong queue
/// row — <see cref="SchedulerService"/> can enqueue a competing <c>Pending</c> row for the same task
/// between the test's own <c>Enqueue</c> and <c>TryClaimNext</c>, and <c>TryClaimNext</c> claims per
/// task, not per run, so it has no way to prefer the row the test meant. See
/// architecture/planning/todo/follow-up-runwatermarktimetests-claims-the-wrong-row-again-via-the-real-scheduler.md.
/// </summary>
public sealed class RunWatermarkApiFactory : TestApiFactory
{
    protected override void ConfigureTestServices(IServiceCollection services)
    {
        var scheduler = services.FirstOrDefault(d =>
            d.ServiceType == typeof(IHostedService) && d.ImplementationType == typeof(SchedulerService));
        if (scheduler is not null)
            services.Remove(scheduler);
    }
}
