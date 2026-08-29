namespace DataSync.Core.Config;

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
    /// Comparisons between this mapping's source and target, run on demand rather than as part of a
    /// pass — see phase 43. Empty for a mapping nobody has asked to verify.
    /// </summary>
    public List<VerificationCheckConfig> Verification { get; set; } = new();
}

/// <summary>
/// What DataSync is allowed to do to the target's shape without being asked.
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
    /// column something else may still be reading, and DataSync is not the thing that should decide
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
