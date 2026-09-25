using System.Data.Common;
using System.Text.Json;
using DbDataSync.Core.Config;
using DbDataSync.Core.Sql;
using DbDataSync.Drivers.Abstractions;
using DbDataSync.Drivers.Generic;

namespace DbDataSync.Drivers.Postgres;

/// <summary>
/// PostgreSQL logical decoding, read as a query: <c>pg_logical_slot_peek_changes</c> through the
/// <c>wal2json</c> output plugin, with the slot's LSN as the watermark — phase 34.
/// <para>
/// The reason this is a reader rather than a subsystem is that it is a **plain SQL function returning
/// rows**. No streaming protocol, no long-lived process, no change to <see cref="IChangeReader"/>, and
/// the LSN stores as a string like every other position.
/// </para>
///
/// <para>
/// **Peek, never get — this is the whole safety argument.** <c>pg_logical_slot_get_changes</c>
/// consumes: once read, those changes are gone from the slot forever. DbDataSync persists a watermark
/// only after the write succeeds, and a consuming read breaks that invariant outright — the slot
/// advances at read time, the write then fails, and the changes are unrecoverable. So the order is:
/// fix the window's end, peek it, stage, write, commit, persist the watermark, and only *then*
/// <see cref="AcknowledgeAsync"/>.
/// </para>
///
/// <para>
/// **A slot is server-side state on somebody's production database, and it can fill their disk.** A
/// slot nobody consumes pins WAL indefinitely. That is why it is created through a previewed
/// provisioning step rather than implicitly, why <see cref="AdvanceSlotOption"/> below is off by
/// default, and why <see cref="PgLogicalSlotStatement.OurSlots"/> exists.
/// </para>
///
/// <para>
/// **At-least-once.** After a crash a change can be re-delivered. The writers here upsert and the
/// reconciling ones replace a scope wholesale, so a duplicate is harmless — worth stating because the
/// instinct is to build deduplication that is not needed. Transactions arrive in commit order and
/// complete: a transaction's changes never straddle a peek boundary, which is a stronger guarantee
/// than the watermark reader can offer.
/// </para>
/// </summary>
public sealed class PgLogicalSlotReader(SqlDialect dialect) : IChangeReader, IPositionAcknowledging, IReadIntentDeclaring, IPositionCapturing
{
    /// <summary>
    /// Which slot this mapping reads. Left empty it is
    /// <see cref="PgLogicalSlotStatement.DefaultSlotName"/> — one slot per source table.
    /// <para>
    /// **Two mappings may share a slot only if neither advances it.** See
    /// <see cref="AdvanceSlotOption"/>: advancing is per-mapping by the shape of
    /// <see cref="IPositionAcknowledging"/>, and a shared slot advanced by whichever mapping ran last
    /// discards changes the others have not read.
    /// </para>
    /// </summary>
    public const string SlotNameOption = "slotName";

    /// <summary>
    /// Whether a successful pass moves the slot forward, letting the source recycle the WAL behind it.
    /// <para>
    /// **Off by default, and the default is the safe one rather than the tidy one** — the same call
    /// <c>TriggerAuditReader</c>'s own pruning option makes, for the same reason: advancing a slot
    /// discards WAL on somebody's production database, and doing it unasked is not this tool's call.
    /// Here there is a second reason. Advancement is per-mapping, because
    /// <see cref="IPositionAcknowledging"/> is; if a slot is shared, the mapping that ran last would
    /// advance it past changes the others have not read yet, and those changes are then unrecoverable.
    /// Turning this on is a statement that this slot belongs to this mapping alone.
    /// </para>
    /// <para>
    /// Left off, the slot's WAL grows until somebody acts on it. That is a disk-space problem an
    /// operator can see coming; the alternative is a correctness problem they cannot.
    /// </para>
    /// </summary>
    public const string AdvanceSlotOption = "advanceSlot";

    public string Kind => PostgresDriverKinds.LogicalSlot;

    /// <summary>
    /// True — and the whole reason to take on a replication slot rather than scanning a watermark
    /// column. Conditional on the source table's <c>REPLICA IDENTITY</c>, which is why provisioning
    /// refuses a table that cannot produce delete records rather than letting this claim be wrong at
    /// run time.
    /// </summary>
    public bool DetectsDeletes => true;

    /// <summary>
    /// <see cref="ReadIntent.Changes"/> and <see cref="ReadIntent.ChangesFromEarliest"/> are the same
    /// read here, and honestly so: a slot's confirmed position *is* the earliest change that still
    /// exists, because everything behind it has been acknowledged and recycled. There is no floor to
    /// go back to that is further back than where the slot already sits.
    /// <para>
    /// <see cref="ReadIntent.ChangesFromLatest"/> adopts the current WAL position without reading
    /// anything, which is safe here in a way it is not for a consuming read: nothing is skipped until
    /// the watermark has been persisted and the slot advanced to it afterwards.
    /// </para>
    /// </summary>
    public IReadOnlySet<ReadIntent> SupportedIntents { get; } = new HashSet<ReadIntent>
    {
        ReadIntent.Changes, ReadIntent.ChangesFromEarliest, ReadIntent.ChangesFromLatest,
    };

    public IReadOnlyList<ParameterDescriptor> Parameters { get; } =
    [
        new()
        {
            Name = SlotNameOption,
            Label = "Replication slot",
            Description =
                "The logical replication slot this mapping reads. Left empty, one named after the " +
                "source table. The slot has to exist — it is created by Apply on the Provisioning tab — " +
                "and a slot that no replication reads pins the source's WAL until it is dropped.",
            Required = false,
        },
        new()
        {
            Name = AdvanceSlotOption,
            Label = "Advance the slot after a successful pass",
            Description =
                "Lets the source recycle the WAL this slot is holding, once the pass that read it has " +
                "written and recorded its position. Without it the slot's WAL grows for as long as " +
                "the replication runs. Only turn this on when the slot belongs to this mapping alone: " +
                "advancing a shared slot discards changes the other mappings have not read.",
            Type = ParameterType.Bool,
            Default = "false",
        },
    ];

    public async Task<CapturedPosition> CapturePositionAsync(
        DbConnection sourceConnection, SourceTableRef source, IReadOnlyDictionary<string, string> options,
        CancellationToken cancellationToken)
    {
        await dialect.UseDatabaseAsync(sourceConnection, source.Database, cancellationToken);
        // No mapping from an LSN to a commit time without decoding the WAL that contains it, which is
        // the work this call exists to avoid — so, unlike Change Tracking's or CDC's, the time is null.
        return new CapturedPosition(await CurrentLsnAsync(sourceConnection, cancellationToken), PositionTimeUtc: null);
    }

    public async Task<ReadResult> ReadChangesAsync(
        DbConnection sourceConnection,
        SourceTableRef source,
        string? previousWatermark,
        ReadIntent intent,
        IReadOnlyList<ColumnMapping> columnMappings,
        string mappingName,
        IReadOnlyList<CachedColumn> sourceColumns,
        IReadOnlyList<RelationshipConfig> relationships,
        IReadOnlyDictionary<string, IReadOnlyList<CachedColumn>> relationshipColumns,
        IReadOnlyDictionary<string, string> options,
        CancellationToken cancellationToken)
    {
        RefuseTransforms(columnMappings, mappingName);

        await dialect.UseDatabaseAsync(sourceConnection, source.Database, cancellationToken);

        var slot = SlotName(options, source);
        var qualified = $"{source.Schema}.{source.Table}";

        // The end of the window, fixed before anything is read — see PgLogicalSlotStatement.CurrentLsn.
        var target = await CurrentLsnAsync(sourceConnection, cancellationToken);
        await RequireUsableSlotAsync(sourceConnection, slot, qualified, previousWatermark, cancellationToken);

        if (intent == ReadIntent.ChangesFromLatest)
        {
            // Nothing is read, and nothing is discarded either: the slot still sits where it did, and
            // only the acknowledgement that follows a successful (empty) pass moves it here.
            return new ReadResult(Empty(), target);
        }

        var schema = BuildSchema(columnMappings, sourceColumns, mappingName, qualified);
        return new ReadResult(
            PeekAsync(sourceConnection, slot, qualified, target, schema, sourceColumns, cancellationToken),
            target);
    }

    public async Task AcknowledgeAsync(
        DbConnection sourceConnection,
        SourceTableRef source,
        string watermark,
        IReadOnlyDictionary<string, string> options,
        CancellationToken cancellationToken)
    {
        if (!Advances(options))
            return;

        await dialect.UseDatabaseAsync(sourceConnection, source.Database, cancellationToken);

        using var cmd = sourceConnection.CreateTimedCommand();
        cmd.CommandText = PgLogicalSlotStatement.AdvanceSlot;
        cmd.AddParameter("@slot", SlotName(options, source));
        cmd.AddParameter("@lsn", watermark);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    // ---- the read ---------------------------------------------------------------------------------

    private async IAsyncEnumerable<ChangeRow> PeekAsync(
        DbConnection sourceConnection,
        string slot,
        string qualifiedTable,
        string uptoLsn,
        ChangeSchema schema,
        IReadOnlyList<CachedColumn> sourceColumns,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var typeByColumn = sourceColumns.ToDictionary(c => c.Name, c => c.NativeType, StringComparer.OrdinalIgnoreCase);

        using var cmd = sourceConnection.CreateTimedCommand();
        cmd.CommandText = PgLogicalSlotStatement.PeekChanges;
        cmd.AddParameter("@slot", slot);
        cmd.AddParameter("@uptoLsn", uptoLsn);
        cmd.AddParameter("@table", qualifiedTable);

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var lsn = reader.GetString(0);
            using var document = JsonDocument.Parse(reader.GetString(1));
            if (ToChangeRow(document.RootElement, schema, typeByColumn, lsn, qualifiedTable) is { } row)
                yield return row;
        }
    }

    /// <summary>
    /// One decoded change, or null for a record that is not one.
    /// <para>
    /// <c>wal2json</c> emits more than inserts, updates and deletes — truncates, logical messages, and
    /// (unless suppressed, as it is here) transaction boundaries. Skipping what is not a row change is
    /// deliberate rather than defensive: a record shape this reader does not understand must not become
    /// a row, and it must not stop the pass either.
    /// </para>
    /// </summary>
    private static ChangeRow? ToChangeRow(
        JsonElement change,
        ChangeSchema schema,
        IReadOnlyDictionary<string, string> typeByColumn,
        string lsn,
        string qualifiedTable)
    {
        if (!change.TryGetProperty("action", out var action) || action.GetString() is not { } verb)
            return null;

        var operation = verb switch
        {
            "I" => ChangeOperation.Insert,
            "U" => ChangeOperation.Update,
            "D" => ChangeOperation.Delete,
            _ => (ChangeOperation?)null,
        };
        if (operation is not { } op)
            return null;

        // A delete carries only `identity` — the REPLICA IDENTITY columns, which for the default is the
        // primary key. Its non-key values are genuinely gone, and a null for them is the same
        // convention every other delete-reporting reader here uses.
        var values = new object?[schema.Count];
        if (change.TryGetProperty("identity", out var identity))
            Fill(values, identity, schema, typeByColumn, lsn, qualifiedTable);
        if (op != ChangeOperation.Delete && change.TryGetProperty("columns", out var columns))
            Fill(values, columns, schema, typeByColumn, lsn, qualifiedTable);

        return new ChangeRow(op, schema, values);
    }

    /// <summary>
    /// Reads one <c>wal2json</c> column array into the row's own positions, converting each value to
    /// the CLR type its Postgres type implies.
    /// <para>
    /// The conversion is the part that matters. JSON has three scalar types where Postgres has a
    /// hundred, so a <c>timestamp</c> arrives as a string and a <c>numeric</c> may arrive as either —
    /// and a staging provider that writes typed values (phase 38's binary <c>COPY</c>) will not accept
    /// a string where the column is a timestamp. The type comes from <c>wal2json</c>'s own per-column
    /// <c>type</c> where it is present, because that is what the WAL actually said, falling back to the
    /// mapping's cached source shape.
    /// </para>
    /// </summary>
    private static void Fill(
        object?[] values,
        JsonElement columns,
        ChangeSchema schema,
        IReadOnlyDictionary<string, string> typeByColumn,
        string lsn,
        string qualifiedTable)
    {
        if (columns.ValueKind != JsonValueKind.Array)
            return;

        foreach (var column in columns.EnumerateArray())
        {
            if (!column.TryGetProperty("name", out var nameElement) || nameElement.GetString() is not { } name)
                continue;
            if (!schema.TryGetOrdinal(name, out var ordinal))
                continue; // A column this mapping does not carry. The slot decodes whole rows.

            var value = column.TryGetProperty("value", out var v) ? v : default;
            if (value.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
            {
                values[ordinal] = null;
                continue;
            }

            var nativeType = column.TryGetProperty("type", out var t) && t.GetString() is { } declared
                ? declared
                : typeByColumn.GetValueOrDefault(name, "text");
            var raw = value.ValueKind switch
            {
                JsonValueKind.String => value.GetString()!,
                JsonValueKind.True => "true",
                JsonValueKind.False => "false",
                _ => value.GetRawText(),
            };

            try
            {
                values[ordinal] = PostgresValues.FromText(nativeType, raw);
            }
            catch (Exception ex) when (ex is FormatException or OverflowException or ArgumentException)
            {
                throw new InvalidOperationException(
                    $"The change at LSN {lsn} on '{qualifiedTable}' carried a value for column " +
                    $"'{name}' that could not be read as {nativeType}: '{raw}'. The decoded WAL and " +
                    $"this mapping's idea of the column's type disagree — refresh the mapping's " +
                    $"metadata if the column was altered. ({ex.Message})", ex);
            }
        }
    }

    // ---- the slot ---------------------------------------------------------------------------------

    private static async Task<string> CurrentLsnAsync(DbConnection connection, CancellationToken cancellationToken)
    {
        using var cmd = connection.CreateTimedCommand();
        cmd.CommandText = PgLogicalSlotStatement.CurrentLsn;
        return (string)(await cmd.ExecuteScalarAsync(cancellationToken))!;
    }

    /// <summary>
    /// Refuses the pass unless the slot is there, is the plugin this reader speaks, and has not moved
    /// past the position this mapping stored.
    /// <para>
    /// **A slot cannot fall behind its own WAL**, so the expired case is narrower here than for Change
    /// Tracking or CDC: it happens when the slot is dropped and recreated, leaving the stored LSN
    /// meaningless. Either way it raises the same <see cref="PositionExpiredException"/>, so it reads
    /// the same to an operator and recovers the same way — by reloading the table.
    /// </para>
    /// </summary>
    private static async Task RequireUsableSlotAsync(
        DbConnection connection, string slot, string qualifiedTable, string? storedWatermark,
        CancellationToken cancellationToken)
    {
        using var cmd = connection.CreateTimedCommand();
        cmd.CommandText = PgLogicalSlotStatement.SlotState;
        cmd.AddParameter("@slot", slot);
        cmd.AddParameter("@stored", (object?)storedWatermark ?? DBNull.Value);

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw new PositionExpiredException(
                qualifiedTable, storedWatermark ?? "(none)", "(the slot no longer exists)",
                $"Logical replication slot '{slot}'");
        }

        var plugin = reader.GetString(0);
        if (!string.Equals(plugin, PgLogicalSlotStatement.Plugin, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Replication slot '{slot}' uses the '{plugin}' output plugin, and this reader decodes " +
                $"'{PgLogicalSlotStatement.Plugin}'. Point this mapping at a slot created with " +
                $"{PgLogicalSlotStatement.Plugin}, or drop and recreate this one — but only if nothing " +
                "else is reading it, because recreating a slot discards everything it was holding.");
        }

        var confirmed = reader.IsDBNull(2) ? null : reader.GetString(2);
        if (storedWatermark is not null && reader.GetBoolean(3))
        {
            throw new PositionExpiredException(
                qualifiedTable, storedWatermark, confirmed ?? "(unknown)", $"Logical replication slot '{slot}'");
        }
    }

    // ---- configuration --------------------------------------------------------------------------

    private static string SlotName(IReadOnlyDictionary<string, string> options, SourceTableRef source) =>
        options.TryGetValue(SlotNameOption, out var configured) && !string.IsNullOrWhiteSpace(configured)
            ? configured.Trim()
            : PgLogicalSlotStatement.DefaultSlotName(source.Schema, source.Table);

    private static bool Advances(IReadOnlyDictionary<string, string> options) =>
        options.TryGetValue(AdvanceSlotOption, out var value) && bool.TryParse(value, out var advance) && advance;

    /// <summary>
    /// Every other reader here builds a <c>SELECT</c> and puts each mapping's transform expression in
    /// its projection. This one has no <c>SELECT</c> to put anything in: the rows come out of the WAL
    /// as they were written. Refusing at the start of the pass is the only honest option — silently
    /// ignoring a transform would mean writing untransformed values into a target whose shape assumes
    /// they were transformed.
    /// </summary>
    private static void RefuseTransforms(IReadOnlyList<ColumnMapping> columnMappings, string mappingName)
    {
        var transformed = columnMappings
            .Where(m => !string.IsNullOrWhiteSpace(m.Transform))
            .Select(m => m.SourceColumn)
            .ToList();
        if (transformed.Count == 0)
            return;

        throw new InvalidOperationException(
            $"Mapping '{mappingName}' transforms {string.Join(", ", transformed.Select(c => $"'{c}'"))}, " +
            $"and the '{PostgresDriverKinds.LogicalSlot}' reader cannot apply a column transform: it " +
            "decodes rows out of the write-ahead log rather than selecting them, so there is no " +
            "projection for the expression to go in. Remove the transform, or read this table with a " +
            "reader that queries it.");
    }

    /// <summary>
    /// The row layout, from what the mapping asked for — or, when it asked for nothing, from the
    /// source's cached shape, which is this reader's equivalent of "return whole rows".
    /// </summary>
    private static ChangeSchema BuildSchema(
        IReadOnlyList<ColumnMapping> columnMappings,
        IReadOnlyList<CachedColumn> sourceColumns,
        string mappingName,
        string qualifiedTable)
    {
        if (columnMappings.Count > 0)
            return new ChangeSchema([.. columnMappings.Select(m => m.SourceColumn).Distinct(StringComparer.OrdinalIgnoreCase)]);

        if (sourceColumns.Count == 0)
            throw new MetadataNotCachedException(mappingName, "source", qualifiedTable);

        return new ChangeSchema([.. sourceColumns.Select(c => c.Name)]);
    }

    private static async IAsyncEnumerable<ChangeRow> Empty()
    {
        await Task.CompletedTask;
        yield break;
    }
}
