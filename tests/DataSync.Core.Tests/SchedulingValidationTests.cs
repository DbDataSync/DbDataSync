using DataSync.Core.Config;

namespace DataSync.Core.Tests;

/// <summary>
/// A continuous worker exits when it has gone a whole idle timeout without a pass finding anything.
/// These pin the two numbers that decide when that is, and the one pairing of them that is a mistake.
/// </summary>
public sealed class SchedulingValidationTests
{
    private static SchedulingConfig Continuous(int? frequency, int? idle = null) =>
        new() { Mode = ScheduleMode.Continuous, FrequencySeconds = frequency, IdleTimeoutSeconds = idle };

    private static void Validate(SchedulingConfig scheduling) =>
        ConfigValidation.ValidateScheduling(scheduling, "sales");

    [Fact]
    public void ATimeoutLongerThanTheFrequency_IsFine() => Validate(Continuous(15, 60));

    /// <summary>
    /// The mistake: the worker gives up before it has looked for changes even once, so it exits after
    /// every pass — the behaviour the timeout exists to stop, restored by a number.
    /// </summary>
    [Theory]
    [InlineData(60, 60)]
    [InlineData(60, 30)]
    public void AnExplicitTimeoutNoLongerThanTheFrequency_IsRefused(int frequency, int idle)
    {
        var problem = Assert.Throws<ConfigValidationException>(() => Validate(Continuous(frequency, idle)));

        Assert.Contains("idle timeout has to be longer than the frequency", problem.Message);
    }

    /// <summary>
    /// **Not** applied to the default. Polling hourly is an ordinary thing to configure, and against
    /// the 60s default it means "run, wait a minute, exit, come back in an hour" — right, and
    /// something the rule above would forbid outright.
    /// </summary>
    [Fact]
    public void TheDefaultTimeout_IsNotHeldToThatRule()
    {
        Validate(Continuous(3600));

        Assert.Equal(TimeSpan.FromSeconds(60), Continuous(3600).IdleTimeout);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void ANonPositiveFrequency_IsRefused(int frequency) =>
        Assert.Throws<ConfigValidationException>(() => Validate(Continuous(frequency)));

    [Fact]
    public void ANonPositiveTimeout_IsRefused() =>
        Assert.Throws<ConfigValidationException>(() => Validate(Continuous(5, 0)));

    /// <summary>A cron replication has no frequency and no resident worker, so neither number applies.</summary>
    [Fact]
    public void APeriodicSchedule_IsNotSubjectToEither() =>
        Validate(new SchedulingConfig { Mode = ScheduleMode.Periodic, CronExpression = "0 * * * *" });

    /// <summary>
    /// An unset frequency falls back to the idle timeout rather than to zero — nobody chose a cadence,
    /// and spinning is a worse guess than waiting.
    /// </summary>
    [Fact]
    public void AnUnsetFrequency_WaitsRatherThanSpins() =>
        Assert.Equal(TimeSpan.FromSeconds(60), Continuous(null).Frequency);
}
