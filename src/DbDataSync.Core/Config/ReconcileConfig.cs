namespace DbDataSync.Core.Config;

/// <summary>
/// Automated delete reconciliation — phase 125, built on phase 124's on-demand
/// <c>KeyReconcile</c>/<c>KeyReconcileDelete</c> sweep. Replication-level with a per-mapping override
/// (<see cref="TableMappingConfig.ReconcileOverride"/>), the same two-level shape
/// <see cref="ChangeProcessingConfig"/> already has — see <see cref="PipelineResolution"/>'s
/// <c>Reconcile*</c> methods for how the two are resolved.
/// </summary>
public sealed class ReconcileConfig
{
    public bool Enabled { get; set; }

    /// <summary>How often a sweep runs on its own, independent of any change activity — reuses
    /// <see cref="SchedulingConfig"/> wholesale (<c>Continuous</c>/<c>Periodic</c>,
    /// <c>FrequencySeconds</c>/<c>CronExpression</c>) rather than a new grammar, so the same evaluator
    /// (<see cref="SchedulingEvaluator"/>) and the same SPA scheduling editor both already know what to
    /// do with it. <see cref="SchedulingConfig.IdleTimeoutSeconds"/> is a worker-lifetime concept and is
    /// simply ignored here. Null means no cadence at all — a sweep only ever runs when
    /// <see cref="AfterChange"/> asks for one, or an operator triggers it by hand (phase 124).</summary>
    public SchedulingConfig? Every { get; set; }

    /// <summary>Whether a Primary pass that just found changes should also ask for a sweep — see
    /// <see cref="AfterChangeEvaluator"/>. <see cref="NoAfterChangeStrategy"/> by default: nothing here
    /// asks for a sweep beyond <see cref="Every"/>'s own cadence.</summary>
    public AfterChangeStrategy AfterChange { get; set; } = new NoAfterChangeStrategy();

    /// <summary>The guard a scheduled sweep runs with — <see cref="RatioDeleteGuard"/> (50%) by
    /// default, the same default phase 124's on-demand trigger uses when nothing is configured.</summary>
    public DeleteGuard DeleteGuard { get; set; } = new RatioDeleteGuard();

    /// <summary>Null means the only real answer, <c>KeyReconcile</c> — present mainly for the same
    /// structural reason <see cref="ChangeProcessingConfig.Reader"/> carries <c>Options</c>, not because
    /// a different Kind is meaningful here.</summary>
    public ReaderConfig? Reader { get; set; }

    /// <summary>Null means <c>StagingTable</c>.</summary>
    public CacheConfig? Cache { get; set; }

    /// <summary>Null means <c>KeyReconcileDelete</c>.</summary>
    public WriterConfig? Writer { get; set; }
}
