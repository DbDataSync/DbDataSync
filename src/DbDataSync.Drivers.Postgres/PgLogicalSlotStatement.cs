namespace DbDataSync.Drivers.Postgres;

/// <summary>
/// The statements <see cref="PgLogicalSlotReader"/> and the slot's provisioning issue — separated from
/// both, like every other statement builder here, so the parts with real defects available in them are
/// assertable without a server.
/// <para>
/// There is more here than usual worth pinning as text. Logical decoding's whole safety argument is
/// which *function* is called (<c>peek</c>, never <c>get</c>), and the difference between the safe one
/// and the one that loses data is four characters.
/// </para>
/// <para>
/// Table aliases carry no <c>AS</c>, following the rule <c>TriggerAuditStatement</c> states — these
/// statements are Postgres-only, which accepts either, but a reader copying the shape into the generic
/// layer should not find a trap here.
/// </para>
/// </summary>
public static class PgLogicalSlotStatement
{
    /// <summary>The output plugin. <c>test_decoding</c> ships with Postgres and is documented as not
    /// being for machine consumption; <c>pgoutput</c> also ships but is the binary replication protocol
    /// and therefore streaming rather than a query. <c>wal2json</c> is the one that keeps a change feed
    /// inside <c>IChangeReader</c>'s existing shape — see the phase doc's own table.</summary>
    public const string Plugin = "wal2json";

    /// <summary>The prefix every slot this tool creates carries, so one can be told from a slot
    /// somebody else's replication put there — which is what makes orphan detection possible at all.</summary>
    public const string SlotNamePrefix = "dbdatasync_";

    /// <summary>
    /// The end of this pass's window, fixed before anything is read.
    /// <para>
    /// Fixing it up front is what makes the watermark safe to persist after the write: the reader
    /// promises to have read everything up to this position and nothing after it, whatever else the
    /// source does while the pass runs.
    /// </para>
    /// </summary>
    public const string CurrentLsn = "SELECT pg_current_wal_lsn()::text;";

    /// <summary>
    /// Everything about the slot that decides whether this pass can run: whether it is still there,
    /// what plugin it has, where it is confirmed to, whether it has moved *past* the stored watermark
    /// (which is the only way changes can be lost), and how much WAL it is currently pinning.
    /// <para>
    /// The comparison happens here rather than in C# on purpose. An LSN prints as <c>0/1A2B3C8</c> —
    /// hexadecimal, and not zero-padded — so ordinal string comparison gets it wrong (<c>0/9</c> sorts
    /// after <c>0/10</c>). <c>pg_lsn</c> compares correctly, and the server is the thing that owns the
    /// type.
    /// </para>
    /// </summary>
    public const string SlotState = """
        SELECT s.plugin,
               s.restart_lsn::text,
               s.confirmed_flush_lsn::text,
               COALESCE(s.confirmed_flush_lsn > @stored::pg_lsn, false) AS past_stored,
               COALESCE(pg_current_wal_lsn() - s.confirmed_flush_lsn, 0)::text AS retained_bytes
        FROM pg_replication_slots s
        WHERE s.slot_name = @slot;
        """;

    /// <summary>
    /// **Peek, never get.** <c>pg_logical_slot_get_changes</c> consumes: once read, those changes are
    /// gone from the slot forever. DbDataSync persists a watermark only after the write succeeds, and a
    /// consuming read breaks that outright — the slot advances at read time, the write then fails, and
    /// the changes are unrecoverable. That is silent data loss, and it shows up only as a target that
    /// quietly disagrees with its source.
    /// <para>
    /// No <c>ORDER BY</c>: the function returns changes in commit order by construction, and sorting a
    /// decoded WAL range would cost a materialisation of the whole window for nothing.
    /// </para>
    /// <para>
    /// <c>format-version 2</c> emits one JSON object per change rather than one per transaction, which
    /// is what makes a row of this result set map onto a <c>ChangeRow</c>. <c>include-transaction
    /// false</c> drops the begin/commit records that would otherwise have to be filtered out anyway.
    /// <c>add-tables</c> is what keeps a slot decoding the whole database down to the one table this
    /// mapping is about.
    /// </para>
    /// </summary>
    public const string PeekChanges = """
        SELECT p.lsn::text, p.data
        FROM pg_logical_slot_peek_changes(
                 @slot::name, @uptoLsn::pg_lsn, NULL,
                 'format-version', '2',
                 'include-transaction', 'false',
                 'add-tables', @table) p;
        """;

    /// <summary>
    /// Moves the slot forward, so the WAL behind it can be recycled. Called only after the write has
    /// committed *and* the watermark has been persisted — see <c>IPositionAcknowledging</c>, whose
    /// ordering guarantee this is the second caller of and the one that makes it load-bearing.
    /// </summary>
    public const string AdvanceSlot = "SELECT pg_replication_slot_advance(@slot::name, @lsn::pg_lsn);";

    /// <summary>
    /// Creating and dropping a slot are rendered as literal statements rather than parameterised ones,
    /// because a provisioning step's <c>CommandText</c> *is* the preview an operator reads before
    /// applying it — a statement showing <c>@slot</c> would be showing something other than what runs.
    /// <see cref="ValidateSlotName"/> is what makes that safe.
    /// </summary>
    public static string RenderCreateSlot(string slot) =>
        $"SELECT pg_create_logical_replication_slot('{ValidateSlotName(slot)}', '{Plugin}');";

    public static string RenderDropSlot(string slot) =>
        $"SELECT pg_drop_replication_slot('{ValidateSlotName(slot)}');";

    /// <summary>
    /// A slot name as Postgres will accept it, or a refusal naming what is wrong with it.
    /// <para>
    /// Postgres restricts a slot name to lower-case letters, digits and underscores, and rejects
    /// anything else outright rather than folding it — so this validates rather than escapes, and an
    /// operator finds out at configuration time instead of at Apply. It is also what lets the create
    /// and drop statements above be rendered as literals: a name that passes this cannot carry a quote.
    /// </para>
    /// </summary>
    public static string ValidateSlotName(string slot)
    {
        if (string.IsNullOrWhiteSpace(slot))
            throw new ArgumentException("A replication slot name is required.", nameof(slot));
        if (slot.Length > 63)
            throw new ArgumentException(
                $"Replication slot name '{slot}' is {slot.Length} characters; Postgres allows 63.", nameof(slot));
        if (!slot.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c == '_'))
            throw new ArgumentException(
                $"Replication slot name '{slot}' is not one Postgres will accept: a slot name may only " +
                "contain lower-case letters, digits and underscores.", nameof(slot));
        return slot;
    }

    /// <summary>
    /// Whether the server will decode WAL at all. <c>wal_level = logical</c> is a restart-requiring
    /// setting and the single biggest adoption obstacle this mechanism has, so it is worth checking
    /// before a slot is proposed rather than after one fails to be created.
    /// </summary>
    public const string WalLevel = "SELECT current_setting('wal_level');";

    /// <summary>
    /// Which output plugin libraries this server will let a slot use.
    /// <para>
    /// A prerequisite that did not exist until recently: PostgreSQL 18.6, 17.11, 16.15, 15.19 and
    /// 14.24 added this allowlist as the fix for CVE-2026-6471, and its default holds only the two
    /// plugins that ship with Postgres. A server that has the <c>wal2json</c> library installed and not
    /// listed here refuses slot creation with <c>library "wal2json" may not be used as an output
    /// plugin</c> — a message that leads nowhere obvious, which is exactly why this is checked before a
    /// slot is proposed rather than left to surface at Apply.
    /// </para>
    /// <para>
    /// <c>missing_ok</c>, because an older minor version has no such setting and is not missing
    /// anything — a null here means "this server does not restrict output plugins", not "it allows
    /// none".
    /// </para>
    /// </summary>
    public const string OutputPluginLibraries = "SELECT current_setting('output_plugin_libraries', true);";

    /// <summary>
    /// Every slot this tool could have created, with what it is pinning. Both halves of the operational
    /// story come from here: the lag of a slot that *is* configured, and the existence of one that is
    /// not — a slot nobody consumes pins WAL indefinitely, which is the failure mode where this tool
    /// takes a production database down.
    /// </summary>
    public const string OurSlots = $"""
        SELECT s.slot_name,
               s.plugin,
               s.database,
               s.active,
               s.restart_lsn::text,
               s.confirmed_flush_lsn::text,
               COALESCE(pg_current_wal_lsn() - s.confirmed_flush_lsn, 0)::text AS retained_bytes
        FROM pg_replication_slots s
        WHERE s.slot_type = 'logical' AND s.slot_name LIKE '{SlotNamePrefix}%'
        ORDER BY s.slot_name;
        """;

    /// <summary>
    /// Whether a table can report its own deletes.
    /// <para>
    /// <c>REPLICA IDENTITY DEFAULT</c> puts the primary key in the delete and update-old record, which
    /// is exactly what a change feed needs — but **a table with no primary key and <c>DEFAULT</c>
    /// produces no delete records at all**. Not an error, not a warning: the deletes simply are not in
    /// the WAL. Discovering that as a target which never loses rows is the worst possible way to find
    /// out, so it is a configuration-time refusal instead.
    /// </para>
    /// <para>
    /// <c>relreplident</c>: <c>d</c> default, <c>n</c> nothing, <c>f</c> full, <c>i</c> a named index.
    /// </para>
    /// </summary>
    public const string ReplicaIdentity = """
        SELECT c.relreplident,
               EXISTS (SELECT 1 FROM pg_index i WHERE i.indrelid = c.oid AND i.indisprimary) AS has_primary_key
        FROM pg_class c
        JOIN pg_namespace n ON n.oid = c.relnamespace
        WHERE n.nspname = @schema AND c.relname = @table;
        """;

    /// <summary>
    /// The default slot name for a source table, when an operator has not stated one.
    /// <para>
    /// Keyed on the *table* rather than on the mapping, because <c>IPositionAcknowledging</c> is handed
    /// a <c>SourceTableRef</c> and no mapping name — so a mapping-derived default would resolve to one
    /// slot when reading and a different one when acknowledging, which is exactly the kind of
    /// near-miss that shows up as a slot that silently never advances.
    /// </para>
    /// <para>
    /// Lower-cased because a slot name may only contain lower-case letters, digits and underscores —
    /// Postgres rejects anything else outright rather than folding it — and everything outside that set
    /// becomes an underscore rather than being dropped, so two tables whose names differ only in
    /// punctuation do not collide.
    /// </para>
    /// </summary>
    public static string DefaultSlotName(string schema, string table)
    {
        var sanitized = new string([.. $"{schema}_{table}".ToLowerInvariant()
            .Select(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) ? c : '_')]);
        // Postgres caps an identifier at 63 bytes and silently truncates beyond it; truncating here
        // instead means the name this tool asks for is the name it later looks for.
        var name = SlotNamePrefix + sanitized;
        return name.Length <= 63 ? name : name[..63];
    }
}
