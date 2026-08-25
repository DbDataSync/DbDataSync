namespace DataSync.Drivers.Abstractions;

public enum ChangeOperation
{
    Insert,
    Update,
    Delete,
}

/// <summary>
/// One row-level change. For <see cref="ChangeOperation.Delete"/>, most readers (e.g. SQL Server
/// Change Tracking) can only recover the primary key columns — the rest of the row is gone by the
/// time it's read — so <see cref="Values"/> may be PK-only for deletes. Writers must handle that.
/// </summary>
public sealed record ChangeRow(ChangeOperation Operation, IReadOnlyDictionary<string, object?> Values);
