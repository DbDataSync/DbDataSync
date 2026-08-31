using DataSync.Drivers.Abstractions;
using DataSync.Core.Sql;

namespace DataSync.Drivers.Generic;

/// <summary>
/// The columns a historizing writer needs beyond the mapped ones.
/// <para>
/// Added to the same <see cref="ProvisioningColumn"/> list <c>CreateTargetTable</c> and
/// <c>AlterTargetTable</c> already build from, rather than through a second provisioning path — these
/// are exactly the "columns the mapping needs that are not one-to-one with the source" that phase 45
/// §8's alter machinery was written to account for.
/// </para>
/// </summary>
public static class HistorizedProvisioning
{
    /// <summary>
    /// The mapped columns, plus whatever the writer adds.
    /// <para>
    /// **The SCD2 surrogate becomes the table's primary key, and the source's own key stops being
    /// one.** A target that keeps every version of a key has that key many times over, so leaving it
    /// as the primary key would make the second version of anything a constraint violation — the
    /// first write, not a later surprise. The natural key stays marked in the mapping and is what the
    /// writer versions by.
    /// </para>
    /// </summary>
    public static IReadOnlyList<ProvisioningColumn> Extend(
        IReadOnlyList<ProvisioningColumn> mapped, string? writerKind) => writerKind switch
    {
        GenericDriverKinds.Snapshot =>
        [
            .. mapped,
            new ProvisioningColumn(
                HistorizedColumns.SnapshotAt, Timestamp, IsNullable: false, IsPrimaryKey: false),
        ],

        GenericDriverKinds.Scd2 =>
        [
            new ProvisioningColumn(
                HistorizedColumns.SurrogateKey, Text, IsNullable: false, IsPrimaryKey: true),
            .. mapped.Select(c => c with { IsPrimaryKey = false }),
            new ProvisioningColumn(
                HistorizedColumns.ValidFrom, Timestamp, IsNullable: false, IsPrimaryKey: false),
            // Nullable, and null is what "still current" means — so an open version is findable
            // without reading the flag, and the two answers cannot disagree.
            new ProvisioningColumn(
                HistorizedColumns.ValidTo, Timestamp, IsNullable: true, IsPrimaryKey: false),
            new ProvisioningColumn(
                HistorizedColumns.IsCurrent, Boolean, IsNullable: false, IsPrimaryKey: false),
        ],

        _ => mapped,
    };

    /// <summary>Whether this writer keeps history, which is what makes a target legitimately carry
    /// more rows than its source — see phase 54.</summary>
    public static bool IsHistorizing(string? writerKind) =>
        writerKind is GenericDriverKinds.Snapshot or GenericDriverKinds.Scd2;

    private static CanonicalType Timestamp { get; } =
        new(CanonicalTypeKind.Timestamp, null, null, null, IsUnicode: false, IsMax: false);

    private static CanonicalType Text { get; } =
        new(CanonicalTypeKind.String, 200, null, null, IsUnicode: false, IsMax: false);

    private static CanonicalType Boolean { get; } =
        new(CanonicalTypeKind.Boolean, null, null, null, IsUnicode: false, IsMax: false);
}
