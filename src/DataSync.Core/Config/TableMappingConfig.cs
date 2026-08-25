namespace DataSync.Core.Config;

public class TableRef
{
    public required string ConnectionName { get; set; }
    public required string Database { get; set; }
    public string Schema { get; set; } = "dbo";
    public required string Table { get; set; }
}

public sealed class SourceTableRef : TableRef
{
    /// <summary>Raw SQL predicate narrowing which rows this reader considers. Never string-concatenated
    /// into generated statements without going through the driver's identifier/parameter validation.</summary>
    public string? Filter { get; set; }
}

public sealed class ColumnMapping
{
    public required string SourceColumn { get; set; }
    public required string TargetColumn { get; set; }
    public string? Transform { get; set; }
}

/// <summary>
/// Persisted as config/replications/&lt;replication&gt;/table-mappings/&lt;name&gt;.yaml. Sources/Targets
/// are lists per architecture/planning/architecture.md's "based on source or target tables, or both" —
/// v1 usage is expected to be 1 source : 1 target, but the shape doesn't foreclose fan-in/fan-out later.
/// </summary>
public sealed class TableMappingConfig
{
    public required string Name { get; set; }
    public required List<SourceTableRef> Sources { get; set; }
    public required List<TableRef> Targets { get; set; }
    public List<ColumnMapping> ColumnMappings { get; set; } = new();
}
