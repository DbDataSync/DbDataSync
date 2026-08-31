using DataSync.Core.Config;
using DataSync.Drivers.Abstractions;
using DataSync.Drivers.Generic;
using Xunit;

namespace DataSync.Drivers.Generic.Tests;

/// <summary>
/// Phase 68's derivation of the SCD Type 2 natural key: the source's primary key, said in the target's
/// column names.
/// </summary>
public sealed class NaturalKeyDerivationTests
{
    private static ColumnMetadata Column(string name, bool key = false) =>
        new(name, "int", IsNullable: false, IsPrimaryKey: key, IsIdentity: false);

    private static ColumnMapping Map(string source, string target) =>
        new() { SourceColumn = source, TargetColumn = target };

    [Fact]
    public void A_single_column_primary_key_derives_to_its_target_name()
    {
        var derived = NaturalKeyDerivation.Derive(
            [Column("OrderId", key: true), Column("Total")],
            [Map("OrderId", "order_id"), Map("Total", "total")]);

        Assert.Equal(["order_id"], derived);
        Assert.Equal("order_id", NaturalKeyDerivation.Format(derived));
    }

    [Fact]
    public void A_composite_primary_key_derives_every_one_of_its_columns()
    {
        var derived = NaturalKeyDerivation.Derive(
            [Column("TenantId", key: true), Column("OrderId", key: true), Column("Total")],
            [Map("TenantId", "tenant_id"), Map("OrderId", "order_id"), Map("Total", "total")]);

        Assert.Equal(["tenant_id", "order_id"], derived);
        Assert.Equal("tenant_id, order_id", NaturalKeyDerivation.Format(derived));
    }

    [Fact]
    public void A_source_with_no_primary_key_derives_nothing()
    {
        var derived = NaturalKeyDerivation.Derive(
            [Column("Name"), Column("Total")],
            [Map("Name", "name"), Map("Total", "total")]);

        Assert.Empty(derived);
    }

    /// <summary>
    /// Half a composite key is not a key. Matching versions on <c>order_id</c> alone when identity is
    /// really <c>(tenant_id, order_id)</c> would collapse two tenants' rows into one dimension member —
    /// silently, and only for the rows where it matters.
    /// </summary>
    [Fact]
    public void A_key_column_the_mapping_does_not_carry_derives_nothing_rather_than_a_partial_key()
    {
        var derived = NaturalKeyDerivation.Derive(
            [Column("TenantId", key: true), Column("OrderId", key: true), Column("Total")],
            [Map("OrderId", "order_id"), Map("Total", "total")]);

        Assert.Empty(derived);
    }

    [Fact]
    public void Source_column_names_are_matched_case_insensitively()
    {
        var derived = NaturalKeyDerivation.Derive(
            [Column("OrderID", key: true)],
            [Map("orderid", "order_id")]);

        Assert.Equal(["order_id"], derived);
    }

    [Fact]
    public void No_column_mappings_at_all_derives_nothing()
    {
        Assert.Empty(NaturalKeyDerivation.Derive([Column("OrderId", key: true)], []));
    }
}
