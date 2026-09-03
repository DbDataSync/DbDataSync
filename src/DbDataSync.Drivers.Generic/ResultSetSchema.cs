using System.Data.Common;
using DbDataSync.Drivers.Abstractions;

namespace DbDataSync.Drivers.Generic;

/// <summary>Builds a <see cref="ChangeSchema"/> from a result set's own column names, and reads a row
/// straight into a positional array. Used by every reader whose result set *is* the source row.</summary>
public static class ResultSetSchema
{
    public static ChangeSchema From(DbDataReader reader) => FromLeading(reader, reader.FieldCount);

    /// <summary>
    /// The first <paramref name="columnCount"/> columns only, for a statement that carries bookkeeping
    /// past the end of the row — a bounded read appending the ordering value it stops at, for
    /// instance. Leading rather than arbitrary, so every ordinal the schema describes is still its own
    /// ordinal in the result set.
    /// </summary>
    public static ChangeSchema FromLeading(DbDataReader reader, int columnCount)
    {
        var names = new string[columnCount];
        for (var i = 0; i < columnCount; i++)
            names[i] = reader.GetName(i);
        return new ChangeSchema(names);
    }

    public static object?[] ReadValues(DbDataReader reader, int columnCount)
    {
        var values = new object?[columnCount];
        for (var i = 0; i < columnCount; i++)
            values[i] = reader.IsDBNull(i) ? null : reader.GetValue(i);
        return values;
    }
}
