namespace DataSync.Drivers.Abstractions;

public sealed record TableMetadata(string Schema, string Table);

public sealed record ColumnMetadata(string Name, string NativeType, bool IsNullable, bool IsPrimaryKey);
