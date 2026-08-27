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
}
