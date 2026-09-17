using DbDataSync.Core.Config;
using DbDataSync.Core.Sql;
using DbDataSync.Drivers.Abstractions;
using Xunit;

namespace DbDataSync.Drivers.Postgres.Tests;

/// <summary>
/// Phase 34's statements and its two configuration-time refusals, without a server.
/// <para>
/// The first test here is the one that matters most in this whole phase, and it is four characters
/// long: <c>peek</c>, not <c>get</c>. <c>pg_logical_slot_get_changes</c> consumes — once read, those
/// changes are gone from the slot forever — and DbDataSync persists a watermark only after the write
/// succeeds. A consuming read turns a failed write into permanent, silent data loss. There is a real
/// integration test for the behaviour too; this one exists because a one-word edit could reintroduce
/// it and nothing else in the file would look different.
/// </para>
/// </summary>
public sealed class PgLogicalSlotStatementTests
{
    [Fact]
    public void TheRead_Peeks_AndNeverConsumes()
    {
        Assert.Contains("pg_logical_slot_peek_changes", PgLogicalSlotStatement.PeekChanges);
        Assert.DoesNotContain("pg_logical_slot_get_changes", PgLogicalSlotStatement.PeekChanges);
    }

    /// <summary>One JSON object per change rather than per transaction is what makes a row of this
    /// result set a <c>ChangeRow</c>; the table filter is what keeps a slot that decodes the whole
    /// database down to the one table a mapping is about.</summary>
    [Fact]
    public void TheRead_AsksForOneObjectPerChange_ScopedToOneTable()
    {
        Assert.Contains("'format-version', '2'", PgLogicalSlotStatement.PeekChanges);
        Assert.Contains("'add-tables', @table", PgLogicalSlotStatement.PeekChanges);
        Assert.Contains("'include-transaction', 'false'", PgLogicalSlotStatement.PeekChanges);
    }

    /// <summary>
    /// The LSN comparison happens in SQL, not in C#. An LSN prints as <c>0/1A2B3C8</c> — hexadecimal
    /// and not zero-padded — so ordinal string comparison gets it wrong: <c>0/9</c> sorts after
    /// <c>0/10</c>. Getting this wrong would mean a stored position that reads as "past the slot" at
    /// random, which surfaces as a spurious demand to reload the table.
    /// </summary>
    [Fact]
    public void TheSlotCheck_ComparesPositionsAsPgLsn_NotAsText()
    {
        Assert.Contains("confirmed_flush_lsn > @stored::pg_lsn", PgLogicalSlotStatement.SlotState);
    }

    [Fact]
    public void CreatingASlot_NamesThePluginThisReaderDecodes()
    {
        Assert.Equal(
            "SELECT pg_create_logical_replication_slot('dbdatasync_public_orders', 'wal2json');",
            PgLogicalSlotStatement.RenderCreateSlot("dbdatasync_public_orders"));
    }

    [Fact]
    public void DroppingASlot_IsRenderedForThePreviewToo() =>
        Assert.Equal(
            "SELECT pg_drop_replication_slot('dbdatasync_public_orders');",
            PgLogicalSlotStatement.RenderDropSlot("dbdatasync_public_orders"));

    /// <summary>
    /// The create and drop statements are rendered as literals — a provisioning step's command text
    /// *is* the preview an operator reads, so one showing <c>@slot</c> would be showing something other
    /// than what runs. Validation is what makes that safe, and it validates rather than escapes because
    /// Postgres rejects anything outside this character set outright rather than folding it.
    /// </summary>
    [Theory]
    [InlineData("Orders", "lower-case")]
    [InlineData("dbdatasync-orders", "lower-case")]
    [InlineData("dbdatasync_orders'; DROP TABLE x --", "lower-case")]
    [InlineData("", "required")]
    public void ASlotNamePostgresWouldReject_IsRefusedRatherThanEscaped(string slot, string expected)
    {
        var ex = Assert.Throws<ArgumentException>(() => PgLogicalSlotStatement.ValidateSlotName(slot));
        Assert.Contains(expected, ex.Message);
    }

    [Fact]
    public void ASlotNameLongerThanPostgresAllows_IsRefusedHereRatherThanTruncatedThere() =>
        Assert.Contains("63", Assert.Throws<ArgumentException>(
            () => PgLogicalSlotStatement.ValidateSlotName(new string('a', 64))).Message);

    /// <summary>
    /// The default is derived from the *table*, not the mapping, and that is load-bearing rather than
    /// arbitrary: <see cref="IPositionAcknowledging"/> is handed a <c>SourceTableRef</c> and no mapping
    /// name, so a mapping-derived default would resolve to one slot when reading and a different one
    /// when acknowledging — a slot that silently never advances.
    /// </summary>
    [Fact]
    public void TheDefaultSlotName_IsDerivedFromTheTable_AndIsOnePostgresAccepts()
    {
        var name = PgLogicalSlotStatement.DefaultSlotName("Public", "Order-Lines");

        Assert.Equal("dbdatasync_public_order_lines", name);
        Assert.Equal(name, PgLogicalSlotStatement.ValidateSlotName(name));
    }

    [Fact]
    public void TheDefaultSlotName_ForALongTable_IsTruncatedToWhatPostgresAccepts()
    {
        var name = PgLogicalSlotStatement.DefaultSlotName("public", new string('t', 200));

        Assert.Equal(63, name.Length);
        Assert.Equal(name, PgLogicalSlotStatement.ValidateSlotName(name));
    }

    /// <summary>
    /// Every other reader here builds a <c>SELECT</c> and puts each mapping's transform expression in
    /// its projection. This one has no <c>SELECT</c> — the rows come out of the write-ahead log as they
    /// were written — so a transform cannot be applied, and silently ignoring one would write
    /// untransformed values into a target whose shape assumes they were transformed.
    /// </summary>
    [Fact]
    public async Task AMappingWithAColumnTransform_IsRefusedByNameRatherThanIgnored()
    {
        var reader = new PgLogicalSlotReader(PostgresDialect.Instance);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => reader.ReadChangesAsync(
            new Npgsql.NpgsqlConnection(),
            new SourceTableRef { ConnectionName = "s", Database = "d", Schema = "public", Table = "orders" },
            "0/1", ReadIntent.Changes,
            [
                new() { SourceColumn = "id", TargetColumn = "id" },
                new() { SourceColumn = "name", TargetColumn = "name", Transform = "upper(name)" },
            ],
            "orders-mapping", [], new Dictionary<string, string>(), CancellationToken.None));

        Assert.Contains("'name'", ex.Message);
        Assert.Contains("orders-mapping", ex.Message);
        Assert.Contains("write-ahead log", ex.Message);
    }

    /// <summary>The claim the whole mechanism is chosen for, declared rather than implied — and
    /// conditional on a REPLICA IDENTITY that provisioning refuses to proceed without.</summary>
    [Fact]
    public void TheReader_DeclaresThatItDetectsDeletes() =>
        Assert.True(new PgLogicalSlotReader(PostgresDialect.Instance).DetectsDeletes);

    /// <summary>
    /// <c>ChangesFromEarliest</c> is declared, and it is the same read as <c>Changes</c>: a slot's
    /// confirmed position *is* the earliest change that still exists, because everything behind it has
    /// been acknowledged and recycled. Declaring it is honest rather than aspirational.
    /// </summary>
    [Fact]
    public void TheReader_DeclaresEveryIntentItCanHonour()
    {
        var reader = new PgLogicalSlotReader(PostgresDialect.Instance);

        Assert.Equal(
            new HashSet<ReadIntent> { ReadIntent.Changes, ReadIntent.ChangesFromEarliest, ReadIntent.ChangesFromLatest },
            reader.SupportedIntents);
    }

    /// <summary>
    /// Advancing is off unless asked for — the same call <c>TriggerAuditReader</c>'s pruning option
    /// makes, and here for a second reason: advancement is per-mapping because
    /// <see cref="IPositionAcknowledging"/> is, so a shared slot advanced by whichever mapping ran last
    /// would discard changes the others have not read.
    /// </summary>
    [Fact]
    public void AdvancingTheSlot_IsOffByDefault()
    {
        var advance = new PgLogicalSlotReader(PostgresDialect.Instance).Parameters
            .Single(p => p.Name == PgLogicalSlotReader.AdvanceSlotOption);

        Assert.Equal("false", advance.Default);
        Assert.Equal(ParameterType.Bool, advance.Type);
    }
}
