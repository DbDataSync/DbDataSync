using DbDataSync.Core.Config;

namespace DbDataSync.Core.Tests;

/// <summary>Phase 124's delete-diff sweep guard — pure arithmetic, and the JSON round trip its
/// options-bag channel relies on. No YAML converter yet: that's phase 125's, once
/// <c>ReconcileConfig</c> gives a guard a persisted home.</summary>
public sealed class DeleteGuardEvaluatorTests
{
    [Fact]
    public void Ratio_UnderTheLimit_IsOk()
    {
        var result = DeleteGuardEvaluator.Check(new RatioDeleteGuard(0.5), scopeCount: 100, deleted: 40);

        Assert.True(result.Ok);
        Assert.Null(result.Message);
    }

    [Fact]
    public void Ratio_AtExactlyTheLimit_IsOk()
    {
        // <= , not < — a sweep that deletes precisely the configured ceiling is not "over" it.
        var result = DeleteGuardEvaluator.Check(new RatioDeleteGuard(0.5), scopeCount: 100, deleted: 50);

        Assert.True(result.Ok);
    }

    [Fact]
    public void Ratio_OverTheLimit_NamesTheObservedRatioAndTheLimit()
    {
        var result = DeleteGuardEvaluator.Check(new RatioDeleteGuard(0.5), scopeCount: 100, deleted: 60);

        Assert.False(result.Ok);
        Assert.Contains("60", result.Message);
        Assert.Contains("100", result.Message);
        Assert.Contains("50", result.Message); // the limit, rendered as a percentage
        Assert.Contains("override", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Ratio_AnEmptyScope_IsOk()
    {
        // Nothing to protect — an empty segment is not a runaway delete, whatever 0/0 would say.
        var result = DeleteGuardEvaluator.Check(new RatioDeleteGuard(0.5), scopeCount: 0, deleted: 0);

        Assert.True(result.Ok);
    }

    [Fact]
    public void None_IsAlwaysOk_EvenDeletingEverything()
    {
        var result = DeleteGuardEvaluator.Check(new NoneDeleteGuard(), scopeCount: 100, deleted: 100);

        Assert.True(result.Ok);
    }

    [Fact]
    public void OptionRoundTrip_RatioGuard_PreservesTheConfiguredMaxRatio()
    {
        var json = DeleteGuardOption.Serialize(new RatioDeleteGuard(0.25));
        var options = new Dictionary<string, string> { [DeleteGuardOption.OptionKey] = json };

        var guard = Assert.IsType<RatioDeleteGuard>(DeleteGuardOption.Read(options));
        Assert.Equal(0.25, guard.MaxRatio);
    }

    [Fact]
    public void OptionRoundTrip_NoneGuard_SurvivesAsNone()
    {
        var json = DeleteGuardOption.Serialize(new NoneDeleteGuard());
        var options = new Dictionary<string, string> { [DeleteGuardOption.OptionKey] = json };

        Assert.IsType<NoneDeleteGuard>(DeleteGuardOption.Read(options));
    }

    [Fact]
    public void Option_AbsentFromOptions_DefaultsToARatioGuardOfOneHalf()
    {
        var guard = Assert.IsType<RatioDeleteGuard>(DeleteGuardOption.Read(new Dictionary<string, string>()));

        Assert.Equal(0.5, guard.MaxRatio);
    }
}
