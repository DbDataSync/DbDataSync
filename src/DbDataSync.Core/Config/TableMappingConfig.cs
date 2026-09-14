namespace DbDataSync.Core.Config;

/// <summary>
/// Where a replication reads from or writes to: a connection and a database. Owned by the replication
/// and inherited by every one of its table mappings (see <see cref="TableSpec"/>).
/// </summary>
public sealed class EndpointRef
{
    public string? ConnectionName { get; set; }
    public string? Database { get; set; }
}

/// <summary>The source and target endpoints every table mapping in a replication inherits.</summary>
public sealed class TaskEndpoints
{
    public EndpointRef? Source { get; set; }
    public EndpointRef? Target { get; set; }
}

/// <summary>
/// One side of a table mapping, as configured. <see cref="ConnectionName"/> and
/// <see cref="Database"/> are null to inherit the replication's endpoint, and set to override it —
/// each independently, so a mapping can point at a different database on the same connection without
/// restating the connection.
/// <para>
/// Nullable-flat rather than a nested optional endpoint object because it makes every config written
/// before endpoints existed read back correctly with no migration: those carry both fields on every
/// mapping, which is exactly what "this mapping overrides" means.
/// </para>
/// <para>
/// This is the *configured* shape. Drivers receive <see cref="TableRef"/>, which is the resolved one —
/// see <see cref="EndpointResolution"/>.
/// </para>
/// </summary>
public class TableSpec
{
    public string? ConnectionName { get; set; }
    public string? Database { get; set; }
    public string Schema { get; set; } = "dbo";
    public required string Table { get; set; }
}

public sealed class SourceTableSpec : TableSpec
{
    /// <summary>Raw SQL predicate narrowing which rows this reader considers. Never string-concatenated
    /// into generated statements without going through the driver's identifier/parameter validation.</summary>
    public string? Filter { get; set; }
}

/// <summary>
/// A fully-resolved table reference — every field known. This is what drivers consume; it deliberately
/// has no notion of inheritance, so no reader or writer has to think about where a value came from.
/// </summary>
public class TableRef
{
    public required string ConnectionName { get; set; }
    public required string Database { get; set; }
    public string Schema { get; set; } = "dbo";
    public required string Table { get; set; }
}

public sealed class SourceTableRef : TableRef
{
    public string? Filter { get; set; }
}

public sealed class ColumnMapping
{
    public required string SourceColumn { get; set; }
    public required string TargetColumn { get; set; }

    /// <summary>
    /// A SQL expression in the *source's* dialect, evaluated by the source engine — not by this process
    /// and not by the target. <see cref="ColumnToken"/> stands for the column being transformed.
    /// <para>
    /// Admin-authored raw SQL, on the same footing as <see cref="SourceTableSpec.Filter"/>: an arbitrary
    /// expression cannot be parameterised, and whoever authors it can already point a connection
    /// anywhere. See phase 22.
    /// </para>
    /// </summary>
    public string? Transform { get; set; }

    /// <summary>
    /// The target column's type, when the operator has chosen one. **Null is the normal case** — the
    /// type is inferred from the source's through the canonical system, and writing that inference
    /// into config would freeze today's answer against a source whose column later changes.
    /// <para>
    /// Written in the *target's* dialect, since that is where it is used.
    /// </para>
    /// </summary>
    public string? TargetType { get; set; }

    /// <summary>
    /// Every rename this column has been through, oldest first.
    /// <para>
    /// A list rather than a "renamed from", because an operator can rename a column more than once
    /// before anything is applied, and provisioning needs the history rather than the latest edit.
    /// </para>
    /// </summary>
    public List<RenameStep> Renames { get; set; } = new();

    /// <summary>
    /// The placeholder a transform uses for its own column, substituted by the reader with a reference
    /// that is correct for the statement being built.
    /// <para>
    /// It has to be a token rather than the bare column name because the reference is not spelled the
    /// same way everywhere: the Change Tracking reader joins the source table under an alias, so the
    /// correct reference there is <c>base.[Region]</c>. An unqualified name happens to resolve for a
    /// non-key column and is ambiguous for a key one — the same expression would work or fail depending
    /// on which column it was put on.
    /// </para>
    /// </summary>
    public const string ColumnToken = "{{column}}";
}

/// <summary>
/// Persisted as config/replications/&lt;replication&gt;/table-mappings/&lt;name&gt;.yaml. Sources/Targets
/// are lists per architecture/planning/done/architecture.md's "based on source or target tables, or both" —
/// v1 usage is expected to be 1 source : 1 target, but the shape doesn't foreclose fan-in/fan-out later.
/// </summary>
public sealed class TableMappingConfig
{
    public required string Name { get; set; }
    public required List<SourceTableSpec> Sources { get; set; }
    public required List<TableSpec> Targets { get; set; }
    public List<ColumnMapping> ColumnMappings { get; set; } = new();

    /// <summary>Scripts bound at this level, keyed by slot (see <c>ScriptSlots</c>). An absent key
    /// inherits from a broader level; a key present with a null value is "explicitly none" and
    /// overrides an inherited binding. See <see cref="ScriptResolution"/>.</summary>
    public Dictionary<string, ScriptBinding?> Scripts { get; set; } = new();

    /// <summary>Hooks bound at this level, keyed by point (see <c>HookPoints</c>). See
    /// <see cref="HookResolution"/>.</summary>
    public Dictionary<string, List<HookConfig>?> Hooks { get; set; } = new();

    public ProvisioningConfig Provisioning { get; set; } = new();

    /// <summary>
    /// What this mapping reads next, in place of the replication's <see cref="ReplicationTaskConfig.DefaultReadIntent"/>.
    /// Resolved by <see cref="ReadIntentResolution"/>, and only for a mapping that has never completed a
    /// pass — once one has, its own stored intent (<c>ChangeWatermarkStore.GetReadState</c>) is the
    /// answer and this is never consulted again for it.
    /// <para>
    /// Null means inherit the replication's answer. **Nullable at both levels, deliberately**: null on
    /// the replication means nobody has said, which resolves to the application default
    /// (<see cref="ReadIntent.InitialLoad"/>) — the same shape <see cref="ProvisioningConfig"/> already
    /// uses, and for the same reason phase 46 found in <c>ReplicationTaskConfig.Enabled</c>. A
    /// non-nullable value sitting at its default is omitted by the YAML serializer, so an explicit
    /// choice equal to the default — including explicitly choosing <see cref="ReadIntent.InitialLoad"/>
    /// on a mapping under a replication defaulting to something else — would silently fail to survive
    /// being written down.
    /// </para>
    /// </summary>
    public ReadIntent? DefaultReadIntent { get; set; }

    /// <summary>
    /// This mapping's own reader, in place of the replication's. Null — the normal case — inherits
    /// <see cref="ChangeProcessingConfig.Reader"/> entirely.
    /// <para>
    /// **Atomic: Kind and Options together, never merged.** The same rule scripts, hooks and
    /// provisioning already follow. Half a stage inherited and half overridden would mean an option
    /// set for one Kind silently surviving onto another.
    /// </para>
    /// </summary>
    public ReaderConfig? ReaderOverride { get; set; }

    /// <summary>This mapping's own staging provider, in place of the replication's. Null inherits.
    /// Independent of <see cref="ReaderOverride"/> and <see cref="WriterOverride"/> — see those.</summary>
    public CacheConfig? CacheOverride { get; set; }

    /// <summary>
    /// This mapping's own writer, in place of the replication's. Null inherits.
    /// <para>
    /// The field phase 68 was actually for: the SCD Type 2 writer's <c>naturalKey</c> names the columns
    /// that identify a row across its versions, and a replication syncing three tables needs three
    /// answers. At the replication it could only ever state one.
    /// </para>
    /// </summary>
    public WriterConfig? WriterOverride { get; set; }

    /// <summary>This mapping's own Bulk Load reader, in place of the replication's <see cref="ReplicationTaskConfig.BulkLoad"/>.
    /// Null inherits. Atomic like <see cref="ReaderOverride"/> — Kind and Options together.</summary>
    public ReaderConfig? BulkLoadReaderOverride { get; set; }

    /// <summary>This mapping's own Bulk Load staging provider, in place of the replication's. Null inherits.</summary>
    public CacheConfig? BulkLoadCacheOverride { get; set; }

    /// <summary>This mapping's own Bulk Load writer, in place of the replication's. Null inherits.</summary>
    public WriterConfig? BulkLoadWriterOverride { get; set; }

    /// <summary>This mapping's own delete-reconciliation settings, in place of the replication's. Null
    /// inherits <see cref="ReplicationTaskConfig.Reconcile"/> entirely — phase 125.</summary>
    public ReconcileConfig? ReconcileOverride { get; set; }

    /// <summary>
    /// Comparisons between this mapping's source and target, run on demand rather than as part of a
    /// pass — see phase 43. Empty for a mapping nobody has asked to verify.
    /// </summary>
    public List<VerificationCheckConfig> Verification { get; set; } = new();

    /// <summary>
    /// How this table divides for a reload — the mapping's own answer, used both as what a scheduled
    /// <c>BatchReload</c> pass processes and as what the Bulk Load form starts from.
    /// <para>
    /// **Empty means Full, no segmenting** — naming what already happened rather than changing it. A
    /// mapping that configures nothing reloads its whole table, which is what the Bulk Load form has
    /// always defaulted to.
    /// </para>
    /// <para>
    /// A list, because "how this table segments" genuinely is one: any number of <c>List</c> entries
    /// and any number of <c>Range</c> entries can sit side by side. A single <c>Auto</c> or
    /// <c>Custom</c> entry is the other shape — one entry that expands into many at reload time,
    /// against the source's real values or the strategy's fresh output.
    /// </para>
    /// <para>
    /// **This replaced the <c>segments</c> reader option, it does not sit beside it.** Segmenting is a
    /// property of the mapping, not something buried in a stringly-typed options bag with no editor of
    /// its own. See phase 58 — a documented breaking change, deliberately without a migration.
    /// </para>
    /// </summary>
    public List<BatchReloadSegment> DefaultSegmenting { get; set; } = new();

    /// <summary>
    /// Records how long each stage of a pass took, onto the run itself — see phase 59.
    /// <para>
    /// Opt-in, per mapping, and **off means no measurement at all** rather than measured-and-discarded:
    /// the reader's row stream is only wrapped when this is set, so a mapping that never asked pays
    /// nothing, not even a delegate call per row.
    /// </para>
    /// <para>
    /// Mapping-level only. Timing is something an operator turns on for the one table that is behaving
    /// oddly, which is not a thing to inherit from a replication.
    /// </para>
    /// </summary>
    public bool TraceTiming { get; set; }

    /// <summary>
    /// The source table's shape as it was when this mapping was last captured or refreshed — see
    /// <see cref="CachedColumn"/> for why a cache exists at all beside <see cref="ColumnMapping"/>'s
    /// deliberate refusal to store types.
    /// <para>
    /// Empty for a mapping created before this existed, and for a query source, which has no catalog
    /// to read. **Nothing reads this yet** — phase 90 builds and refreshes the cache; switching
    /// readers off their live catalog queries onto it is a behaviour change with its own phase.
    /// </para>
    /// </summary>
    public List<CachedColumn> SourceColumns { get; set; } = new();

    /// <summary>The target table's shape, on the same terms as <see cref="SourceColumns"/>. Empty for
    /// a target that provisioning has yet to create — there is no catalog entry to capture.</summary>
    public List<CachedColumn> TargetColumns { get; set; } = new();

    /// <summary>
    /// When <see cref="SourceColumns"/>/<see cref="TargetColumns"/> were last written. Null while
    /// nothing has ever been captured.
    /// <para>
    /// The field that makes the cache's own premise checkable. The whole design is that staleness is
    /// something an operator decides about rather than something that happens quietly, and an
    /// operator cannot decide whether to refresh a picture whose age is not on screen.
    /// </para>
    /// <para>
    /// A <see cref="DateTime"/> in UTC, not a <c>DateTimeOffset</c>: YamlDotNet writes a
    /// <c>DateTimeOffset</c> out as a mapping of its own properties and then cannot read it back —
    /// "Property 'dateTime' not found on type 'System.DateTimeOffset'" — so the field would appear to
    /// work until the next time this config was loaded. The offset would be noise anyway; there is
    /// exactly one moment being recorded and it is always UTC.
    /// </para>
    /// </summary>
    public DateTime? ColumnsCapturedUtc { get; set; }

    /// <summary>
    /// Markdown notes about this table mapping specifically — the quirks of this table, why a column is
    /// mapped the way it is, what broke last time. Git-tracked and diffed like the replication's own
    /// (see <c>ReplicationTaskConfig.Notes</c>).
    /// <para>
    /// Not inherited from the replication. A note that applied to forty mappings would be a note about
    /// the replication, and that field already exists.
    /// </para>
    /// </summary>
    public string? Notes { get; set; }
}

/// <summary>
/// One column of a source or target table, as the catalog described it when somebody last looked.
/// <para>
/// The same five facts <c>DbDataSync.Drivers.Abstractions.ColumnMetadata</c> carries, restated here
/// because config cannot reference the driver abstractions — that project references this one, not
/// the other way round — and because a persisted type has to round-trip: YamlDotNet constructs
/// through a parameterless constructor and property setters, so a positional record serialises out
/// and then throws on the way back in. The same reason <see cref="RenameStep"/> is a class.
/// </para>
/// <para>
/// **This is a cache, and it is only ever written by somebody asking for it.** Capture happens when
/// a mapping is saved with a side whose table changed (or that has nothing cached yet), reusing the
/// column list the editor already fetched to draw its picker rather than issuing a query of its own,
/// and the Refresh Metadata action overwrites it on demand. No pass, no run, and no ordinary re-save
/// updates it — a cache that silently caught up with a schema change would be exactly the staleness
/// hazard <see cref="ColumnMapping.TargetType"/>'s doc comment refuses.
/// </para>
/// </summary>
public sealed class CachedColumn
{
    public CachedColumn() { }

    public CachedColumn(string name, string nativeType, bool isNullable, bool isPrimaryKey, bool isIdentity)
    {
        Name = name;
        NativeType = nativeType;
        IsNullable = isNullable;
        IsPrimaryKey = isPrimaryKey;
        IsIdentity = isIdentity;
    }

    public string Name { get; set; } = "";
    public string NativeType { get; set; } = "";
    public bool IsNullable { get; set; }
    public bool IsPrimaryKey { get; set; }
    public bool IsIdentity { get; set; }

    /// <summary>Everything about this column except its name, for reporting what a refresh changed.
    /// Name is excluded because it is the identity a diff pairs two columns *by*.</summary>
    public bool SameShapeAs(CachedColumn other) =>
        string.Equals(NativeType, other.NativeType, StringComparison.Ordinal)
        && IsNullable == other.IsNullable
        && IsPrimaryKey == other.IsPrimaryKey
        && IsIdentity == other.IsIdentity;

    /// <summary>
    /// Whether two captures of a side are the same picture — the question every writer of this cache
    /// asks before deciding it has nothing to record.
    /// <para>
    /// Order-sensitive, and by name as well as shape: a catalog's column order is part of what was
    /// captured, so a table whose columns were reordered is a table whose picture changed. One
    /// implementation because the three callers must agree — a save deciding "unchanged" on a rule a
    /// provisioning report disagrees with would restamp a capture nobody took.
    /// </para>
    /// </summary>
    public static bool SameShape(IReadOnlyList<CachedColumn> a, IReadOnlyList<CachedColumn> b) =>
        a.Count == b.Count
        && a.Zip(b).All(pair =>
            string.Equals(pair.First.Name, pair.Second.Name, StringComparison.Ordinal)
            && pair.First.SameShapeAs(pair.Second));
}

/// <summary>
/// What DbDataSync is allowed to do to the target's shape without being asked.
/// <para>
/// The same type at both levels, and **nullable at both**: on a table mapping null means "inherit the
/// replication's answer", and on the replication it means "nobody has said", which resolves to off.
/// One type rather than two, because "the replication's default" and "this mapping's override" are the
/// same question asked at two levels — the shape phase 16 established for endpoints.
/// </para>
/// <para>
/// Nullable is also what makes <c>false</c> survive being written down: a non-nullable <c>bool</c>
/// whose default is <c>false</c> is omitted by the YAML serializer, which is the bug phase 46 found in
/// <c>Enabled</c>. Here <c>default(bool?)</c> is null, so an explicit <c>false</c> is written and means
/// what it says — "this mapping overrides the replication and says no".
/// </para>
/// </summary>
public sealed class ProvisioningConfig
{
    /// <summary>Target-side, and only when the table is absent entirely. Checked once per pass, before
    /// the segment loop: if resolved on and the target table is absent, the mapping's
    /// <c>createTargetTable</c> plan is applied and every statement it ran is logged at Info.</summary>
    public bool? CreateTargetTableIfMissing { get; set; }

    /// <summary>
    /// Target-side schema evolution, for a table that already exists: add a mapped column the target
    /// lacks, and change one whose type no longer matches.
    /// <para>
    /// **Additive and modifying only, never <c>DROP</c>** — the same restraint
    /// <see cref="CreateTargetTableIfMissing"/> follows. A column the mapping stopped writing is a
    /// column something else may still be reading, and DbDataSync is not the thing that should decide
    /// otherwise.
    /// </para>
    /// </summary>
    public bool? AlterTargetTableColumnsIfMissingOrChanged { get; set; }
}

/// <summary>
/// What a mapping's provisioning settings actually resolve to, once the replication's defaults are
/// applied. Mirrors <see cref="EndpointResolution"/>: each setting falls back independently, so a
/// mapping can override one and inherit the other.
/// </summary>
public static class ProvisioningResolution
{
    public static bool CreateTargetTableIfMissing(ReplicationTaskConfig? task, TableMappingConfig? mapping) =>
        mapping?.Provisioning.CreateTargetTableIfMissing
        ?? task?.Provisioning.CreateTargetTableIfMissing
        ?? false;

    public static bool AlterTargetTableColumns(ReplicationTaskConfig? task, TableMappingConfig? mapping) =>
        mapping?.Provisioning.AlterTargetTableColumnsIfMissingOrChanged
        ?? task?.Provisioning.AlterTargetTableColumnsIfMissingOrChanged
        ?? false;

    /// <summary>Where a resolved value came from, for a UI that wants to show INHERITED.</summary>
    public static BindingLevel LevelOfCreate(TableMappingConfig? mapping) =>
        mapping?.Provisioning.CreateTargetTableIfMissing is null ? BindingLevel.Replication : BindingLevel.Mapping;

    public static BindingLevel LevelOfAlter(TableMappingConfig? mapping) =>
        mapping?.Provisioning.AlterTargetTableColumnsIfMissingOrChanged is null
            ? BindingLevel.Replication
            : BindingLevel.Mapping;
}

/// <summary>
/// One rename of a target column, and whether the target has caught up with it.
/// <para>
/// A class with settable properties rather than a positional record, like every other persisted
/// config type here: the YAML deserializer constructs through a parameterless constructor and
/// property setters, so a record round-trips out to disk and then throws on the way back in.
/// </para>
/// </summary>
public sealed class RenameStep
{
    public RenameStep() { }

    public RenameStep(string from, string to, bool applied = false)
    {
        From = from;
        To = to;
        Applied = applied;
    }

    public string From { get; set; } = "";
    public string To { get; set; } = "";

    /// <summary>
    /// False until provisioning has run the <c>RENAME</c>. A record of what happened rather than a
    /// latch: the planner reads the target's actual columns, so a step left <c>false</c> after
    /// somebody renamed the column by hand plans nothing, and one wrongly marked <c>true</c> does not
    /// stop the rename being planned. What it buys is a history an operator can read.
    /// </summary>
    public bool Applied { get; set; }
}
