using System.Data.Common;
using DbDataSync.Drivers.Abstractions;
using DbDataSync.Drivers.Generic;

namespace DbDataSync.Drivers.Postgres;

/// <summary>
/// Converts a segment's string bound into a parameter typed to match the column it's compared against.
/// Binding a bound as text would make Postgres convert on the *column* side of the comparison, which
/// both changes the comparison's semantics and prevents an index seek — the same reason the MSSQL
/// driver types its bounds, restated because the type enum is the part that does not generalise.
/// </summary>
internal sealed class PostgresValueBinding : ISegmentValueBinder
{
    public static PostgresValueBinding Instance { get; } = new();

    private PostgresValueBinding() { }

    public DbParameter CreateParameter(string name, string rawValue, ColumnMetadata column)
    {
        try
        {
            // Which type the column is, and how to read a string as one, were a single switch here
            // until other callers needed each half on its own — see PostgresNpgsqlTypes (phase 38,
            // typing a COPY cell) and PostgresValues (phase 34, reading a decoded WAL value).
            var npgsqlType = PostgresNpgsqlTypes.Of(column.NativeType);
            return new Npgsql.NpgsqlParameter(name, npgsqlType) { Value = PostgresValues.FromText(npgsqlType, rawValue) };
        }
        catch (Exception ex) when (ex is FormatException or OverflowException or ArgumentException)
        {
            throw new InvalidOperationException(
                $"Segment bound '{rawValue}' is not a valid value for column '{column.Name}' " +
                $"({column.NativeType}): {ex.Message}", ex);
        }
    }
}
