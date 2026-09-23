using System.Collections;
using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;

namespace DbDataSync.Drivers.Jdbc.Ado;

// New for phase 165V — the imported ClrKernel provider never had parameter support (JdbcCommand's
// DbParameterCollection/CreateDbParameter both threw). A plain, storage-only DbParameter: JdbcCommand
// does its own binding at execute time (see JdbcTypeMapping), so there is nothing engine-specific to put
// here beyond holding the values GenericValueBinder already sets (ParameterName, DbType, Value).
internal sealed class JdbcParameter : DbParameter
{
    public override DbType DbType { get; set; } = DbType.String;
    public override ParameterDirection Direction { get; set; } = ParameterDirection.Input;
    public override bool IsNullable { get; set; } = true;
    [AllowNull] public override string ParameterName { get; set; } = "";
    [AllowNull] public override string SourceColumn { get; set; } = "";
    public override object? Value { get; set; }
    public override bool SourceColumnNullMapping { get; set; }
    public override int Size { get; set; }
    public override void ResetDbType() => DbType = DbType.String;
}

// A minimal list-backed DbParameterCollection — JDBC's own PreparedStatement has no notion of a
// provider-side parameter collection at all, so this exists purely to satisfy DbCommand's shape;
// JdbcCommand reads it by index/name itself.
internal sealed class JdbcParameterCollection : DbParameterCollection
{
    private readonly List<JdbcParameter> _items = [];

    public override int Count => _items.Count;
    public override object SyncRoot { get; } = new();

    public override int Add(object value)
    {
        _items.Add((JdbcParameter)value);
        return _items.Count - 1;
    }

    public override void AddRange(Array values)
    {
        foreach (var value in values)
            Add(value!);
    }

    public override void Clear() => _items.Clear();

    public override bool Contains(object value) => _items.Contains((JdbcParameter)value);

    public override bool Contains(string parameterName) => IndexOf(parameterName) >= 0;

    public override void CopyTo(Array array, int index) =>
        ((ICollection)_items).CopyTo(array, index);

    public override IEnumerator GetEnumerator() => _items.GetEnumerator();

    protected override DbParameter GetParameter(int index) => _items[index];

    protected override DbParameter GetParameter(string parameterName) =>
        _items[RequireIndexOf(parameterName)];

    public override int IndexOf(object value) => _items.IndexOf((JdbcParameter)value);

    public override int IndexOf(string parameterName) =>
        _items.FindIndex(p => string.Equals(p.ParameterName, parameterName, StringComparison.Ordinal));

    public override void Insert(int index, object value) => _items.Insert(index, (JdbcParameter)value);

    public override void Remove(object value) => _items.Remove((JdbcParameter)value);

    public override void RemoveAt(int index) => _items.RemoveAt(index);

    public override void RemoveAt(string parameterName) => _items.RemoveAt(RequireIndexOf(parameterName));

    protected override void SetParameter(int index, DbParameter value) => _items[index] = (JdbcParameter)value;

    protected override void SetParameter(string parameterName, DbParameter value) =>
        _items[RequireIndexOf(parameterName)] = (JdbcParameter)value;

    private int RequireIndexOf(string parameterName)
    {
        var index = IndexOf(parameterName);
        return index >= 0 ? index : throw new IndexOutOfRangeException($"Parameter '{parameterName}' not found.");
    }
}
