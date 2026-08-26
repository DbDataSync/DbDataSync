namespace DataSync.Drivers.Abstractions;

public sealed record TableMetadata(string Schema, string Table);

/// <summary><paramref name="IsIdentity"/> is auto-detected from the catalog rather than declared by
/// an operator: a writer that inserts an explicit value into an identity column has to bracket the
/// insert with SET IDENTITY_INSERT, and asking whoever authors a table mapping to remember that is a
/// silent-data-loss trap.</summary>
public sealed record ColumnMetadata(string Name, string NativeType, bool IsNullable, bool IsPrimaryKey, bool IsIdentity);
