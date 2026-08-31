using DataSync.Api.Configuration;
using Microsoft.Extensions.Configuration;

namespace DataSync.Api.Tests;

/// <summary>
/// How retention reads from config. The interesting cases are the two silences: nothing configured
/// (which must be a policy, not "keep everything forever") and an explicit 0 (which must be the
/// recoverable reading of a typo, not the one that empties the table).
/// </summary>
public sealed class RunRetentionOptionsTests
{
    private static ApiOptions Read(params (string Key, string Value)[] settings) =>
        ApiOptions.FromConfiguration(new ConfigurationBuilder()
            .AddInMemoryCollection(settings.Select(s =>
                new KeyValuePair<string, string?>($"DataSync:{s.Key}", s.Value)))
            .Build());

    [Fact]
    public void UnsetMeansTheDefaults_NotUnlimited()
    {
        // A state database that only ever grows is not a policy anyone chose; it is what happens when
        // nobody chooses one, and the cost lands months later.
        var options = Read();

        Assert.Equal(90, options.RunRetentionDays);
        Assert.Equal(1_000, options.RunRetentionMaxPerMapping);
    }

    [Theory]
    [InlineData("30", 30)]
    [InlineData("1", 1)]
    public void AConfiguredValueIsUsed(string configured, int expected) =>
        Assert.Equal(expected, Read(("RunRetentionDays", configured)).RunRetentionDays);

    /// <summary>
    /// Zero is the operator turning a cap off, not asking for everything to be deleted. Between two
    /// readings of the same typo, the recoverable one wins.
    /// </summary>
    [Theory]
    [InlineData("0")]
    [InlineData("-5")]
    [InlineData("not a number")]
    public void ZeroOrNonsenseMeansNoCap(string configured)
    {
        Assert.Null(Read(("RunRetentionDays", configured)).RunRetentionDays);
        Assert.Null(Read(("RunRetentionMaxPerMapping", configured)).RunRetentionMaxPerMapping);
    }

    [Fact]
    public void TheCapsAreIndependent()
    {
        var options = Read(("RunRetentionDays", "0"), ("RunRetentionMaxPerMapping", "50"));

        Assert.Null(options.RunRetentionDays);
        Assert.Equal(50, options.RunRetentionMaxPerMapping);
    }

    [Fact]
    public void ThePruningIntervalDefaultsToHourly_AndIsConfigurable()
    {
        Assert.Equal(TimeSpan.FromHours(1), Read().RunPruningInterval);
        Assert.Equal(TimeSpan.FromMinutes(15), Read(("RunPruningIntervalMinutes", "15")).RunPruningInterval);
        // Nonsense falls back rather than producing a zero-length timer, which PeriodicTimer rejects.
        Assert.Equal(TimeSpan.FromHours(1), Read(("RunPruningIntervalMinutes", "0")).RunPruningInterval);
    }
}
