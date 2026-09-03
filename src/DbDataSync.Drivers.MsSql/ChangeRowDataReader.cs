using System.Collections;
using System.Data;
using System.Data.Common;
using DbDataSync.Drivers.Abstractions;

namespace DbDataSync.Drivers.MsSql;

/// <summary>
/// Adapts an <see cref="IAsyncEnumerable{ChangeRow}"/> to a <see cref="DbDataReader"/> so
/// <see cref="Microsoft.Data.SqlClient.SqlBulkCopy"/> can stream rows into the staging table without
/// materializing the whole change set in memory. Column order is <paramref name="targetColumns"/>
/// followed by a trailing operation-marker column ("__Operation": 'I'/'U'/'D').
/// <para>
/// Each target column's source ordinal is resolved once, against the first row's schema, and every
/// subsequent cell is an array index. Resolving it per cell — which a name-keyed row forces — was the
/// single largest cost in the read path.
/// </para>
/// </summary>
internal sealed class ChangeRowDataReader(
    IAsyncEnumerable<ChangeRow> rows,
    IReadOnlyList<string> targetColumns,
    IReadOnlyDictionary<string, string> sourceColumnByTarget,
    CancellationToken cancellationToken) : DbDataReader
{
    private readonly IAsyncEnumerator<ChangeRow> _enumerator = rows.GetAsyncEnumerator(cancellationToken);
    private ChangeRow? _current;
    private int[]? _sourceOrdinalByTarget;
    private bool _finished;

    public long RowsProduced { get; private set; }

    public override int FieldCount => targetColumns.Count + 1;
    public override bool HasRows => true;
    public override int Depth => 0;
    public override bool IsClosed => _finished;
    public override int RecordsAffected => -1;

    public override object this[int ordinal] => GetValue(ordinal);
    public override object this[string name] => GetValue(GetOrdinal(name));

    public override string GetName(int ordinal) =>
        ordinal < targetColumns.Count ? targetColumns[ordinal] : "__Operation";

    public override int GetOrdinal(string name)
    {
        for (var i = 0; i < targetColumns.Count; i++)
            if (string.Equals(targetColumns[i], name, StringComparison.OrdinalIgnoreCase))
                return i;
        if (string.Equals(name, "__Operation", StringComparison.OrdinalIgnoreCase))
            return targetColumns.Count;
        throw new IndexOutOfRangeException(name);
    }

    public override object GetValue(int ordinal)
    {
        if (ordinal == targetColumns.Count)
            return OperationCode(_current!.Operation);

        return _current!.Values[_sourceOrdinalByTarget![ordinal]] ?? DBNull.Value;
    }

    /// <summary>
    /// Maps each target column to its ordinal in the source row, once, from the first row's schema.
    /// <para>
    /// A mapping naming a source column the reader doesn't produce throws here. It used to be a
    /// silent dictionary miss that wrote NULL into the target — which for a NOT NULL column surfaced
    /// much later as a constraint violation naming a column nobody had touched, and for a nullable one
    /// never surfaced at all.
    /// </para>
    /// </summary>
    private int[] ResolveOrdinalsFor(ChangeSchema schema)
    {
        var map = new int[targetColumns.Count];
        for (var i = 0; i < targetColumns.Count; i++)
            map[i] = schema.GetOrdinal(sourceColumnByTarget[targetColumns[i]]);
        return map;
    }

    public override bool IsDBNull(int ordinal) => GetValue(ordinal) is DBNull;

    public override async Task<bool> ReadAsync(CancellationToken ct)
    {
        if (_finished)
            return false;

        if (!await _enumerator.MoveNextAsync())
        {
            _finished = true;
            return false;
        }

        _current = _enumerator.Current;
        _sourceOrdinalByTarget ??= ResolveOrdinalsFor(_current.Schema);
        RowsProduced++;
        return true;
    }

    public override bool Read() => ReadAsync(CancellationToken.None).GetAwaiter().GetResult();

    public override bool NextResult() => false;

    private static string OperationCode(ChangeOperation operation) => operation switch
    {
        ChangeOperation.Insert => "I",
        ChangeOperation.Update => "U",
        ChangeOperation.Delete => "D",
        _ => throw new ArgumentOutOfRangeException(nameof(operation)),
    };

    public override int GetValues(object[] values)
    {
        var count = Math.Min(values.Length, FieldCount);
        for (var i = 0; i < count; i++)
            values[i] = GetValue(i);
        return count;
    }

    public override string GetString(int ordinal) => (string)GetValue(ordinal);
    public override bool GetBoolean(int ordinal) => (bool)GetValue(ordinal);
    public override byte GetByte(int ordinal) => (byte)GetValue(ordinal);
    public override char GetChar(int ordinal) => (char)GetValue(ordinal);
    public override DateTime GetDateTime(int ordinal) => (DateTime)GetValue(ordinal);
    public override decimal GetDecimal(int ordinal) => (decimal)GetValue(ordinal);
    public override double GetDouble(int ordinal) => (double)GetValue(ordinal);
    public override float GetFloat(int ordinal) => (float)GetValue(ordinal);
    public override Guid GetGuid(int ordinal) => (Guid)GetValue(ordinal);
    public override short GetInt16(int ordinal) => (short)GetValue(ordinal);
    public override int GetInt32(int ordinal) => (int)GetValue(ordinal);
    public override long GetInt64(int ordinal) => (long)GetValue(ordinal);
    public override string GetDataTypeName(int ordinal) => GetFieldType(ordinal).Name;
    public override Type GetFieldType(int ordinal) => _current is null ? typeof(object) : GetValue(ordinal)?.GetType() ?? typeof(object);

    public override long GetBytes(int ordinal, long dataOffset, byte[]? buffer, int bufferOffset, int length) =>
        throw new NotSupportedException();

    public override long GetChars(int ordinal, long dataOffset, char[]? buffer, int bufferOffset, int length) =>
        throw new NotSupportedException();

    public override IEnumerator GetEnumerator() => throw new NotSupportedException();

    public override ValueTask DisposeAsync() => _enumerator.DisposeAsync();
}
