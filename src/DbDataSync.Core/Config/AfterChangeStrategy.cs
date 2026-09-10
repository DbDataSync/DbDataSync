using System.Text.Json.Serialization;

namespace DbDataSync.Core.Config;

/// <summary>
/// Whether a mapping's <see cref="ReconcileConfig"/> should sweep in response to changes a Primary pass
/// just found, rather than only on its own <see cref="ReconcileConfig.Every"/> cadence — phase 125. The
/// same sealed-hierarchy shape <see cref="DeleteGuard"/> and <see cref="BatchReloadSegment"/> already
/// establish: every mode carries only the fields valid for it.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "mode")]
[JsonDerivedType(typeof(NoAfterChangeStrategy), "none")]
[JsonDerivedType(typeof(AfterAnyChangeStrategy), "any")]
public abstract record AfterChangeStrategy;

/// <summary>Only the cadence in <see cref="ReconcileConfig.Every"/> triggers a sweep — the default.</summary>
public sealed record NoAfterChangeStrategy : AfterChangeStrategy;

/// <summary>Any row read by a successful Primary pass since the last sweep is enough to trigger
/// another one, floored by the cadence (a sweep never fires more often than <see cref="ReconcileConfig.Every"/>
/// allows).</summary>
public sealed record AfterAnyChangeStrategy : AfterChangeStrategy;

/// <summary>Pure evaluation, assertable without a server or a clock.</summary>
public static class AfterChangeEvaluator
{
    public static bool ShouldReconcile(AfterChangeStrategy strategy, long rowsReadSinceLastSweep) => strategy switch
    {
        NoAfterChangeStrategy => false,
        AfterAnyChangeStrategy => rowsReadSinceLastSweep > 0,
        _ => throw new ArgumentOutOfRangeException(nameof(strategy), strategy, "Unknown after-change strategy."),
    };
}
