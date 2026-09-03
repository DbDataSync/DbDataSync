using System.Data.Common;

namespace DbDataSync.State;

/// <summary>
/// Reading numbers back, without caring which width the engine chose to store them at.
/// <para>
/// SQLite has one integer type. SQL Server and Postgres have several, and their providers are strict:
/// <c>GetInt32</c> on a <c>BIGINT</c> column throws an <c>InvalidCastException</c> rather than
/// widening. Since the schema's own choice of width is an implementation detail of each engine's DDL,
/// insisting on it at every call site would make ten readers depend on it.
/// </para>
/// <para>
/// This is deliberately not a general "read anything" helper: it converts numbers, and only numbers.
/// A column whose type is genuinely wrong should still fail.
/// </para>
/// </summary>
public static class StateReaderExtensions
{
    public static int Int32(this DbDataReader reader, int ordinal) =>
        Convert.ToInt32(reader.GetValue(ordinal));

    public static long Int64(this DbDataReader reader, int ordinal) =>
        Convert.ToInt64(reader.GetValue(ordinal));

    public static int? NullableInt32(this DbDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.Int32(ordinal);

    public static long? NullableInt64(this DbDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.Int64(ordinal);
}
