using DbDataSync.Core.Config;

namespace DbDataSync.Core.Tests;

/// <summary>Phase 125's after-change trigger — pure, and its JSON/YAML round trips.</summary>
public sealed class AfterChangeEvaluatorTests
{
    [Fact]
    public void None_NeverReconciles_EvenWithManyRowsRead() =>
        Assert.False(AfterChangeEvaluator.ShouldReconcile(new NoAfterChangeStrategy(), rowsReadSinceLastSweep: 1000));

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    [InlineData(500, true)]
    public void Any_ReconcilesOnlyWhenSomethingWasRead(long rowsRead, bool expected) =>
        Assert.Equal(expected, AfterChangeEvaluator.ShouldReconcile(new AfterAnyChangeStrategy(), rowsRead));

    [Fact]
    public void JsonRoundTrip_PreservesEachStrategy()
    {
        var options = new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web);

        var none = System.Text.Json.JsonSerializer.Serialize<AfterChangeStrategy>(new NoAfterChangeStrategy(), options);
        Assert.IsType<NoAfterChangeStrategy>(System.Text.Json.JsonSerializer.Deserialize<AfterChangeStrategy>(none, options));

        var any = System.Text.Json.JsonSerializer.Serialize<AfterChangeStrategy>(new AfterAnyChangeStrategy(), options);
        Assert.IsType<AfterAnyChangeStrategy>(System.Text.Json.JsonSerializer.Deserialize<AfterChangeStrategy>(any, options));
    }
}
