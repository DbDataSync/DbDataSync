using DbDataSync.Core.Config;
using DbDataSync.Core.Sql;
using DbDataSync.Drivers.Abstractions;
using Npgsql;
using Xunit;

namespace DbDataSync.Drivers.Postgres.Tests;

/// <summary>
/// Phase 34 against a real server with <c>wal_level = logical</c> and the <c>wal2json</c> output
/// plugin — see <c>docker/postgres-logical/Dockerfile</c>, which exists because no well-known public
/// image carries that plugin any more.
/// <para>
/// The test that matters most here is <see cref="AReadThatIsNotAcknowledged_SeesTheSameChangesAgain"/>.
/// Everything else proves the reader works; that one proves the failure path is closed. DbDataSync
/// persists a watermark only after the write succeeds, so a read that consumed would turn a failed
/// write into permanent data loss — and the difference between the safe function and the one that
/// loses data is four characters.
/// </para>
/// </summary>
[Trait("Category", "Integration")]
public sealed class PgLogicalSlotTests(PostgresTestDatabase db) : IClassFixture<PostgresTestDatabase>, IAsyncLifetime
{
    private readonly PgLogicalSlotReader _reader = new(PostgresDialect.Instance);

    private NpgsqlConnection _source = null!;
    private string _table = null!;
    private string _slot = null!;

    private const string MappingName = "pg-logical-slot";

    private static readonly List<ColumnMapping> Mappings =
    [
        new() { SourceColumn = "id", TargetColumn = "id" },
        new() { SourceColumn = "name", TargetColumn = "name" },
        new() { SourceColumn = "amount", TargetColumn = "amount" },
        new() { SourceColumn = "modified_at", TargetColumn = "modified_at" },
    ];

    private static List<CachedColumn> Columns() =>
    [
        new("id", "integer", false, true, false),
        new("name", "text", false, false, false),
        new("amount", "numeric(18,2)", true, false, false),
        new("modified_at", "timestamp", true, false, false),
    ];

    public async Task InitializeAsync()
    {
        _source = db.OpenConnection();
        _table = $"wal_{Guid.NewGuid():N}";
        _slot = PgLogicalSlotStatement.DefaultSlotName("public", _table);

        await ExecuteAsync($"""
            CREATE TABLE public."{_table}" (
                id integer primary key, name text not null, amount numeric(18,2), modified_at timestamp);
            """);
    }

    /// <summary>
    /// Slots are dropped here rather than left to the fixture, and not for tidiness: Postgres refuses
    /// to drop a database that still has a logical replication slot on it, so a leaked slot would take
    /// the whole class's scratch database with it.
    /// </summary>
    public async Task DisposeAsync()
    {
        try
        {
            await ExecuteAsync($"""
                SELECT pg_drop_replication_slot(slot_name) FROM pg_replication_slots
                WHERE slot_name LIKE '{PgLogicalSlotStatement.SlotNamePrefix}%' AND NOT active;
                """);
        }
        catch (PostgresException)
        {
            // Best-effort teardown, matching the other integration fixtures here.
        }
        await _source.DisposeAsync();
    }

    private async Task ExecuteAsync(string sql)
    {
        await using var cmd = _source.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task<T> ScalarAsync<T>(string sql)
    {
        await using var cmd = _source.CreateCommand();
        cmd.CommandText = sql;
        return (T)(await cmd.ExecuteScalarAsync())!;
    }

    private async Task CreateSlotAsync() => await ExecuteAsync(PgLogicalSlotStatement.RenderCreateSlot(_slot));

    private SourceTableRef Source() =>
        new() { ConnectionName = "src", Database = db.DatabaseName, Schema = "public", Table = _table };

    private static Dictionary<string, string> Options(bool advance = false) =>
        new() { [PgLogicalSlotReader.AdvanceSlotOption] = advance ? "true" : "false" };

    private sealed record Change(ChangeOperation Operation, object? Id, object? Name, object? Amount, object? ModifiedAt);

    private async Task<(List<Change> Changes, string Watermark)> ReadAsync(
        string? previousWatermark, ReadIntent intent = ReadIntent.Changes, bool advance = false)
    {
        var result = await _reader.ReadChangesAsync(
            _source, Source(), previousWatermark, intent, Mappings, MappingName, Columns(), Options(advance),
            CancellationToken.None);

        var changes = new List<Change>();
        await foreach (var row in result.Rows)
            changes.Add(new Change(row.Operation, row.Values[0], row.Values[1], row.Values[2], row.Values[3]));
        return (changes, result.WatermarkAfterRead);
    }

    // ---- the read ---------------------------------------------------------------------------------

    /// <summary>
    /// Every operation the mechanism is chosen for, in one pass — and, the part a watermark scan cannot
    /// do at all, a delete arriving with its key. Its other columns are genuinely gone: <c>REPLICA
    /// IDENTITY DEFAULT</c> puts the primary key in the delete record and nothing else, which is the
    /// same convention every other delete-reporting reader here follows.
    /// </summary>
    [Fact]
    public async Task InsertUpdateAndDelete_AllReachTheReader_WithTheDeleteCarryingOnlyItsKey()
    {
        await CreateSlotAsync();

        await ExecuteAsync($"""
            INSERT INTO public."{_table}" VALUES (1, 'Alice', 10.50, TIMESTAMP '2026-01-01 09:00:00');
            INSERT INTO public."{_table}" VALUES (2, 'Bob', 20.25, TIMESTAMP '2026-01-02 09:00:00');
            UPDATE public."{_table}" SET name = 'Alicia' WHERE id = 1;
            DELETE FROM public."{_table}" WHERE id = 2;
            """);

        var (changes, _) = await ReadAsync(previousWatermark: null);

        Assert.Equal(
            [ChangeOperation.Insert, ChangeOperation.Insert, ChangeOperation.Update, ChangeOperation.Delete],
            changes.Select(c => c.Operation));
        Assert.Equal("Alicia", changes[2].Name);

        var delete = changes[3];
        Assert.Equal(2, delete.Id);
        Assert.Null(delete.Name);
        Assert.Null(delete.Amount);
        Assert.Null(delete.ModifiedAt);
    }

    /// <summary>
    /// The values arrive as the CLR types their Postgres types imply, not as the three scalar types
    /// JSON has. That is not cosmetic: phase 38's binary <c>COPY</c> staging writes a cell in the
    /// column's own wire format and will not take a string where the column is a timestamp, so a
    /// reader that handed back <c>wal2json</c>'s strings verbatim would work through one staging
    /// provider and fail through the other.
    /// </summary>
    [Fact]
    public async Task DecodedValues_ComeBackAsTheTypesTheColumnsAre()
    {
        await CreateSlotAsync();
        await ExecuteAsync($"""
            INSERT INTO public."{_table}" VALUES (7, 'Seven', 12.34, TIMESTAMP '2026-03-04 05:06:07');
            """);

        var change = Assert.Single((await ReadAsync(previousWatermark: null)).Changes);

        Assert.Equal(7, Assert.IsType<int>(change.Id));
        Assert.Equal("Seven", Assert.IsType<string>(change.Name));
        Assert.Equal(12.34m, Assert.IsType<decimal>(change.Amount));
        Assert.Equal(new DateTime(2026, 3, 4, 5, 6, 7), Assert.IsType<DateTime>(change.ModifiedAt));
        Assert.Equal(DateTimeKind.Unspecified, ((DateTime)change.ModifiedAt!).Kind);
    }

    /// <summary>A change to a table the slot also decodes, but this mapping is not about, is filtered
    /// out by the read rather than by the caller — which is what keeps one slot per table from being
    /// the only workable arrangement.</summary>
    [Fact]
    public async Task AChangeToAnotherTable_IsNotInThisMappingsRead()
    {
        await CreateSlotAsync();
        var other = $"other_{Guid.NewGuid():N}";
        await ExecuteAsync($"""
            CREATE TABLE public."{other}" (id integer primary key);
            INSERT INTO public."{other}" VALUES (99);
            INSERT INTO public."{_table}" VALUES (1, 'mine', NULL, NULL);
            """);

        var change = Assert.Single((await ReadAsync(previousWatermark: null)).Changes);
        Assert.Equal(1, change.Id);
    }

    // ---- the safety argument ----------------------------------------------------------------------

    /// <summary>
    /// **The test this phase exists to be trusted by.** A window is read and the pass then fails — no
    /// acknowledgement, no watermark stored — and the next read sees exactly the same changes. Had the
    /// reader used <c>pg_logical_slot_get_changes</c>, they would be gone, and the only symptom would
    /// be a target that quietly disagrees with its source.
    /// </summary>
    [Fact]
    public async Task AReadThatIsNotAcknowledged_SeesTheSameChangesAgain()
    {
        await CreateSlotAsync();
        await ExecuteAsync($"""
            INSERT INTO public."{_table}" VALUES (1, 'Alice', NULL, NULL), (2, 'Bob', NULL, NULL);
            """);

        var (first, watermark) = await ReadAsync(previousWatermark: null);
        Assert.Equal(2, first.Count);

        // The write fails here. Nothing is acknowledged, and nothing is stored.

        var (second, _) = await ReadAsync(previousWatermark: null);
        Assert.Equal(first.Select(c => c.Id), second.Select(c => c.Id));
        Assert.NotEmpty(watermark);
    }

    /// <summary>
    /// Acknowledging is what lets the source recycle the WAL behind the slot, and it happens only after
    /// a pass has written and stored its position. Once it has, those changes are gone — which is the
    /// point, and is why it is not done before.
    /// </summary>
    [Fact]
    public async Task Acknowledging_AdvancesTheSlot_SoTheNextReadStartsAfterIt()
    {
        await CreateSlotAsync();
        await ExecuteAsync($"INSERT INTO public.\"{_table}\" VALUES (1, 'Alice', NULL, NULL);");

        var (first, watermark) = await ReadAsync(previousWatermark: null);
        Assert.Single(first);

        await _reader.AcknowledgeAsync(_source, Source(), watermark, Options(advance: true), CancellationToken.None);

        Assert.Empty((await ReadAsync(watermark)).Changes);
    }

    /// <summary>
    /// The default, and the safe one: a pass that succeeds does not move the slot unless it was asked
    /// to. Advancement is per-mapping because <c>IPositionAcknowledging</c> is, so a shared slot
    /// advanced by whichever mapping ran last would discard changes the others have not read — and
    /// unread WAL is a disk-space problem an operator can see coming, where that is a correctness
    /// problem they cannot.
    /// </summary>
    [Fact]
    public async Task WithoutTheOption_AcknowledgingDoesNotMoveTheSlot()
    {
        await CreateSlotAsync();
        await ExecuteAsync($"INSERT INTO public.\"{_table}\" VALUES (1, 'Alice', NULL, NULL);");

        var (_, watermark) = await ReadAsync(previousWatermark: null);
        await _reader.AcknowledgeAsync(_source, Source(), watermark, Options(advance: false), CancellationToken.None);

        Assert.Single((await ReadAsync(watermark)).Changes);
    }

    /// <summary>
    /// A slot cannot fall behind its own WAL, so the expired case is narrower here than for Change
    /// Tracking or CDC: it is the slot being dropped and recreated — or simply dropped — leaving the
    /// stored position meaningless. It raises the same exception either way, so it reads the same to an
    /// operator and recovers the same way.
    /// </summary>
    [Fact]
    public async Task ASlotDroppedOutFromUnderAStoredPosition_RaisesPositionExpired()
    {
        await CreateSlotAsync();
        await ExecuteAsync($"INSERT INTO public.\"{_table}\" VALUES (1, 'Alice', NULL, NULL);");
        var (_, watermark) = await ReadAsync(previousWatermark: null);

        await ExecuteAsync(PgLogicalSlotStatement.RenderDropSlot(_slot));

        var ex = await Assert.ThrowsAsync<PositionExpiredException>(() => ReadAsync(watermark));
        Assert.Contains(_slot, ex.Mechanism);
        Assert.Equal(watermark, ex.StoredPosition);
    }

    /// <summary>
    /// A slot recreated behind a stored position is the same failure, reached the way an operator
    /// actually reaches it: the new slot starts at the server's current position, which is past
    /// everything the old one held.
    /// </summary>
    [Fact]
    public async Task ASlotRecreatedAheadOfAStoredPosition_RaisesPositionExpired()
    {
        await CreateSlotAsync();
        await ExecuteAsync($"INSERT INTO public.\"{_table}\" VALUES (1, 'Alice', NULL, NULL);");
        var (_, watermark) = await ReadAsync(previousWatermark: null);

        await ExecuteAsync(PgLogicalSlotStatement.RenderDropSlot(_slot));
        await ExecuteAsync($"INSERT INTO public.\"{_table}\" VALUES (2, 'Bob', NULL, NULL);");
        await CreateSlotAsync();

        await Assert.ThrowsAsync<PositionExpiredException>(() => ReadAsync(watermark));
    }

    /// <summary>
    /// Adopting the current position without reading anything, which is safe here in a way it would not
    /// be for a consuming read: the slot is not moved by the read at all, only by the acknowledgement
    /// that follows a successful pass.
    /// </summary>
    [Fact]
    public async Task ChangesFromLatest_ReadsNothing_AndTheSlotOnlyMovesWhenAcknowledged()
    {
        await CreateSlotAsync();
        await ExecuteAsync($"INSERT INTO public.\"{_table}\" VALUES (1, 'skipped', NULL, NULL);");

        var (changes, watermark) = await ReadAsync(previousWatermark: null, ReadIntent.ChangesFromLatest);
        Assert.Empty(changes);

        // Not yet skipped — nothing has been acknowledged, so the change is still there to be had.
        Assert.Single((await ReadAsync(previousWatermark: null)).Changes);

        await _reader.AcknowledgeAsync(_source, Source(), watermark, Options(advance: true), CancellationToken.None);
        Assert.Empty((await ReadAsync(watermark)).Changes);
    }

    /// <summary>Capturing a position reads nothing and creates nothing — what a Bulk Load takes ahead of
    /// itself so the first incremental pass knows where to resume from.</summary>
    [Fact]
    public async Task CapturingAPosition_ReturnsTheCurrentLsn()
    {
        var captured = await _reader.CapturePositionAsync(
            _source, Source(), new Dictionary<string, string>(), CancellationToken.None);

        Assert.Matches("^[0-9A-F]+/[0-9A-F]+$", captured.Position);
        Assert.Null(captured.PositionTimeUtc);
    }

    // ---- configuration time -----------------------------------------------------------------------

    private ProvisioningRequest Request(string table, IReadOnlyDictionary<string, string>? options = null) =>
        new(ProvisioningActions.EnableSourceChangeCapture,
            new TableRef { ConnectionName = "src", Database = db.DatabaseName, Schema = "public", Table = table },
            PostgresDriverKinds.LogicalSlot,
            options ?? new Dictionary<string, string>(),
            []);

    /// <summary>
    /// The refusal that keeps this reader's <c>DetectsDeletes</c> claim honest. A table with no primary
    /// key and <c>REPLICA IDENTITY DEFAULT</c> produces **no delete records at all** — not an error,
    /// not a warning; Postgres simply does not write them — so a target would quietly keep rows the
    /// source no longer has. Discovering that as a replication that never loses rows is the worst
    /// possible way to find out.
    /// </summary>
    [Fact]
    public async Task ATableThatCannotReportItsDeletes_IsRefusedAtConfigurationTime()
    {
        var keyless = $"keyless_{Guid.NewGuid():N}";
        await ExecuteAsync($"CREATE TABLE public.\"{keyless}\" (id integer, name text);");

        var plan = await PostgresProvisioner.PlanAsync(_source, Request(keyless), CancellationToken.None);

        Assert.Equal(ProvisioningState.Unsupported, plan.State);
        Assert.Empty(plan.Steps);
        var warning = Assert.Single(plan.Warnings);
        Assert.Contains("no primary key", warning);
        Assert.Contains("REPLICA IDENTITY", warning);
        // And it says what to do about it, including the thing not to do reflexively.
        Assert.Contains("not recommended", warning);
    }

    /// <summary>
    /// The plan proposes the slot once and is satisfied afterwards — an Apply that ran twice must not
    /// try to create it again, because <c>pg_create_logical_replication_slot</c> on an existing name is
    /// an error rather than a no-op.
    /// </summary>
    [Fact]
    public async Task TheSlot_IsPlannedOnceAndSatisfiedAfterwards()
    {
        var before = await PostgresProvisioner.PlanAsync(_source, Request(_table), CancellationToken.None);

        Assert.Equal(ProvisioningState.Missing, before.State);
        var step = Assert.Single(before.Steps);
        Assert.Equal(PgLogicalSlotStatement.RenderCreateSlot(_slot), step.CommandText);
        Assert.Equal(ProvisioningStepScope.Database, step.Scope);
        // The preview says what the operator is taking on, including that nothing drops it for them.
        Assert.Contains("fill the source's disk", step.Rationale);

        await ExecuteAsync(step.CommandText);

        var after = await PostgresProvisioner.PlanAsync(_source, Request(_table), CancellationToken.None);
        Assert.Equal(ProvisioningState.Satisfied, after.State);
        Assert.Empty(after.Steps);
    }

    /// <summary>An operator-named slot is what the plan proposes and what the reader then reads — the
    /// two resolving it differently is the failure that would show up as a slot nothing ever advances.</summary>
    [Fact]
    public async Task AnOperatorNamedSlot_IsTheOneBothThePlanAndTheReaderUse()
    {
        var named = $"dbdatasync_named_{Guid.NewGuid():N}"[..40];
        var options = new Dictionary<string, string> { [PgLogicalSlotReader.SlotNameOption] = named };

        var plan = await PostgresProvisioner.PlanAsync(_source, Request(_table, options), CancellationToken.None);
        Assert.Equal(PgLogicalSlotStatement.RenderCreateSlot(named), Assert.Single(plan.Steps).CommandText);

        await ExecuteAsync(Assert.Single(plan.Steps).CommandText);
        await ExecuteAsync($"INSERT INTO public.\"{_table}\" VALUES (1, 'Alice', NULL, NULL);");

        var result = await _reader.ReadChangesAsync(
            _source, Source(), null, ReadIntent.Changes, Mappings, MappingName, Columns(), options,
            CancellationToken.None);
        var rows = new List<ChangeRow>();
        await foreach (var row in result.Rows)
            rows.Add(row);

        Assert.Single(rows);
    }

    /// <summary>
    /// A slot on the wrong output plugin is refused rather than read, and the refusal says why dropping
    /// it is not an obvious fix — something else may be reading it, and recreating a slot discards
    /// everything it holds.
    /// </summary>
    [Fact]
    public async Task ASlotOnAnotherOutputPlugin_IsRefusedByName()
    {
        await ExecuteAsync($"SELECT pg_create_logical_replication_slot('{_slot}', 'test_decoding');");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => ReadAsync(previousWatermark: null));

        Assert.Contains("test_decoding", ex.Message);
        Assert.Contains("wal2json", ex.Message);
    }

    /// <summary>The environment this whole phase needs, asserted rather than assumed — if the container
    /// stops carrying the plugin, this is the test that says so instead of eleven others failing with
    /// "could not access file".</summary>
    [Fact]
    public async Task TheTestServer_DecodesLogicallyAndHasTheOutputPlugin()
    {
        Assert.Equal("logical", await ScalarAsync<string>(PgLogicalSlotStatement.WalLevel));

        await CreateSlotAsync();
        Assert.Equal(
            PgLogicalSlotStatement.Plugin,
            await ScalarAsync<string>($"SELECT plugin FROM pg_replication_slots WHERE slot_name = '{_slot}';"));
    }
}
