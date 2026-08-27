using System.Data.Common;
using DataSync.Drivers.Abstractions;

namespace DataSync.Drivers.MsSql;

/// <summary>Builds a <see cref="ChangeSchema"/> from a result set's own column names, and reads a row
/// straight into a positional array. Used by every reader whose result set *is* the source row.</summary>
internal static class ResultSetSchema
{
    public static ChangeSchema From(DbDataReader reader)
    {
        var names = new string[reader.FieldCount];
        for (var i = 0; i < names.Length; i++)
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
