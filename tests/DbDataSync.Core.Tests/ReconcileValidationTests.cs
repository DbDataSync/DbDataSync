using DbDataSync.Core.Config;

namespace DbDataSync.Core.Tests;

/// <summary>Phase 125's <see cref="ConfigValidation.ValidateReconcile"/> — mirrors
/// <see cref="KeyReconcilePairingValidationTests"/>'s own shape, one level up. Phase 129 widens both
/// with the mapping's own primary writer Kind/Options, forwarded verbatim to
/// <see cref="ConfigValidation.ValidateKeyReconcilePairing"/>.</summary>
public sealed class ReconcileValidationTests
{
    private static readonly IReadOnlyDictionary<string, string> NoOptions = new Dictionary<string, string>();

    private static TableMappingConfig Mapping(
        IReadOnlyList<CachedColumn>? sourceColumns = null, IReadOnlyList<ColumnMapping>? columnMappings = null) => new()
    {
        Name = "orders",
        Sources = [new SourceTableSpec { ConnectionName = "s", Database = "d", Schema = "dbo", Table = "Orders" }],
        Targets = [new TableSpec { ConnectionName = "t", Database = "d", Schema = "dbo", Table = "Orders" }],
        SourceColumns = sourceColumns is null ? [] : [.. sourceColumns],
        ColumnMappings = columnMappings is null ? [] : [.. columnMappings],
    };

    private static readonly IReadOnlyList<CachedColumn> KeyedSource = [new CachedColumn("Id", "int", false, true, false)];
    private static readonly IReadOnlyList<ColumnMapping> KeyMapped = [new ColumnMapping { SourceColumn = "Id", TargetColumn = "Id" }];

    [Fact]
    public void Disabled_IsNeverThisChecksBusiness_EvenWithAWildlyInvalidRestOfIt()
    {
        // Enabled: false with garbage everywhere else still passes — nothing here is checked unless a
        // sweep might actually run.
        var reconcile = new ReconcileConfig { Enabled = false, AfterChange = new AfterAnyChangeStrategy() };
        ConfigValidation.ValidateReconcile(reconcile, Mapping(), "BatchReload", "DeleteInsert", "MsSqlMerge", NoOptions);
    }

    [Fact]
    public void Enabled_WithTheKeyReconcilePair_AndAFullyMappedKey_Passes()
    {
        var reconcile = new ReconcileConfig { Enabled = true };
        ConfigValidation.ValidateReconcile(
            reconcile, Mapping(KeyedSource, KeyMapped), "KeyReconcile", "KeyReconcileDelete", "MsSqlMerge", NoOptions);
    }

    [Fact]
    public void Enabled_WithAMismatchedPair_IsRejected()
    {
        var reconcile = new ReconcileConfig { Enabled = true };
        var ex = Assert.Throws<ConfigValidationException>(() =>
            ConfigValidation.ValidateReconcile(
                reconcile, Mapping(KeyedSource, KeyMapped), "KeyReconcile", "DeleteInsert", "MsSqlMerge", NoOptions));

        Assert.Contains("KeyReconcile", ex.Message);
    }

    [Fact]
    public void Enabled_WithAKeylessSource_IsRejected()
    {
        var reconcile = new ReconcileConfig { Enabled = true };
        var noKey = new[] { new CachedColumn("Id", "int", false, false, false) };
        var ex = Assert.Throws<ConfigValidationException>(() =>
            ConfigValidation.ValidateReconcile(
                reconcile, Mapping(noKey, KeyMapped), "KeyReconcile", "KeyReconcileDelete", "MsSqlMerge", NoOptions));

        Assert.Contains("no primary key", ex.Message);
    }

    [Fact]
    public void Enabled_WithAnAfterChangeStrategy_ButNoEvery_IsRejected()
    {
        var reconcile = new ReconcileConfig { Enabled = true, AfterChange = new AfterAnyChangeStrategy(), Every = null };
        var ex = Assert.Throws<ConfigValidationException>(() =>
            ConfigValidation.ValidateReconcile(
                reconcile, Mapping(KeyedSource, KeyMapped), "KeyReconcile", "KeyReconcileDelete", "MsSqlMerge", NoOptions));

        Assert.Contains("cadence", ex.Message);
    }

    [Fact]
    public void Enabled_WithAnAfterChangeStrategy_AndAnEvery_Passes()
    {
        var reconcile = new ReconcileConfig
        {
            Enabled = true,
            AfterChange = new AfterAnyChangeStrategy(),
            Every = new SchedulingConfig { Mode = ScheduleMode.Continuous, FrequencySeconds = 300 },
        };
        ConfigValidation.ValidateReconcile(
            reconcile, Mapping(KeyedSource, KeyMapped), "KeyReconcile", "KeyReconcileDelete", "MsSqlMerge", NoOptions);
    }

    [Fact]
    public void Enabled_WithNoAfterChangeStrategy_AndNoEvery_Passes()
    {
        // A purely on-demand sweep (phase 124's trigger only) — a real, supported configuration.
        var reconcile = new ReconcileConfig { Enabled = true, Every = null };
        ConfigValidation.ValidateReconcile(
            reconcile, Mapping(KeyedSource, KeyMapped), "KeyReconcile", "KeyReconcileDelete", "MsSqlMerge", NoOptions);
    }

    [Fact]
    public void Enabled_WithAnInvalidEveryCadence_IsRejectedByTheExistingSchedulingRules()
    {
        var reconcile = new ReconcileConfig
        {
            Enabled = true,
            Every = new SchedulingConfig { Mode = ScheduleMode.Continuous, FrequencySeconds = 0 },
        };
        Assert.Throws<ConfigValidationException>(() =>
            ConfigValidation.ValidateReconcile(
                reconcile, Mapping(KeyedSource, KeyMapped), "KeyReconcile", "KeyReconcileDelete", "MsSqlMerge", NoOptions));
    }

    [Fact]
    public void Enabled_WithScd2CloseAndAScd2PrimaryWriter_AndNoStatedKey_Passes()
    {
        var reconcile = new ReconcileConfig { Enabled = true };
        ConfigValidation.ValidateReconcile(
            reconcile, Mapping(KeyedSource, KeyMapped), "KeyReconcile", "KeyReconcileScd2Close", "Scd2", NoOptions);
    }

    [Fact]
    public void Enabled_WithScd2Close_ButPrimaryWriterIsNotScd2_IsRejected()
    {
        var reconcile = new ReconcileConfig { Enabled = true };
        var ex = Assert.Throws<ConfigValidationException>(() =>
            ConfigValidation.ValidateReconcile(
                reconcile, Mapping(KeyedSource, KeyMapped), "KeyReconcile", "KeyReconcileScd2Close", "MsSqlMerge", NoOptions));

        Assert.Contains("Scd2", ex.Message);
    }

    [Fact]
    public void Enabled_WithScd2Close_AndAStatedKeyEqualToTheDerivedOne_Passes()
    {
        var reconcile = new ReconcileConfig { Enabled = true };
        var options = new Dictionary<string, string> { ["naturalKey"] = "Id" };
        ConfigValidation.ValidateReconcile(
            reconcile, Mapping(KeyedSource, KeyMapped), "KeyReconcile", "KeyReconcileScd2Close", "Scd2", options);
    }

    [Fact]
    public void Enabled_WithScd2Close_AndAStatedKeyNamingADifferentColumn_IsRejected()
    {
        var reconcile = new ReconcileConfig { Enabled = true };
        var options = new Dictionary<string, string> { ["naturalKey"] = "SomeOtherColumn" };
        var ex = Assert.Throws<ConfigValidationException>(() =>
            ConfigValidation.ValidateReconcile(
                reconcile, Mapping(KeyedSource, KeyMapped), "KeyReconcile", "KeyReconcileScd2Close", "Scd2", options));

        Assert.Contains("SomeOtherColumn", ex.Message);
        Assert.Contains("Id", ex.Message);
    }
}
