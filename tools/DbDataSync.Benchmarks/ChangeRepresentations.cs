using System.Buffers;
using System.Collections;
using System.Data.Common;

namespace DbDataSync.Benchmarks;

/// <summary>The candidate in-memory shapes for a batch of changes.</summary>
public enum Representation
{
    /// <summary>What DbDataSync does today: a pre-sized <c>Dictionary&lt;string, object?&gt;</c> per row,
    /// read back by column name — the shape <c>ChangeRow.Values</c> carries.</summary>
    Dictionary,

    /// <summary>A positional <c>object?[]</c> per row with ordinals resolved once per run.</summary>
    RowArray,

    /// <summary>Hand-rolled columnar batches over pooled, natively-typed arrays. Values are stored
    /// unboxed; a box is created only if the consumer asks for one, per cell, and dies immediately.</summary>
    Columnar,
}

/// <summary>Shared <see cref="DbDataReader"/> plumbing so each representation only supplies storage.</summary>
public abstract class RepresentationReader(int columnCount) : DbDataReader
{
    protected int ColumnCount { get; } = columnCount;

    public override int FieldCount => ColumnCount;
    public override bool HasRows => true;
    public override int Depth => 0;
    public override int RecordsAffected => -1;
    public override bool IsClosed => false;

    public override string GetName(int ordinal) => BenchmarkSchema.NameOf(ordinal);
    public override int GetOrdinal(string name) => int.Parse(name.AsSpan(3));
    public override object this[int ordinal] => GetValue(ordinal);
    public override object this[string name] => GetValue(GetOrdinal(name));
    public override bool IsDBNull(int ordinal) => false;
    public override bool NextResult() => false;
    public override Task<bool> ReadAsync(CancellationToken cancellationToken) => Task.FromResult(Read());
    public override Type GetFieldType(int ordinal) => BenchmarkSchema.ClrTypeOf(ordinal);
    public override string GetDataTypeName(int ordinal) => GetFieldType(ordinal).Name;

    public override int GetValues(object[] values)
    {
        var count = Math.Min(values.Length, FieldCount);
        for (var i = 0; i < count; i++)
            values[i] = GetValue(i);
        return count;
    }

    public override string GetString(int ordinal) => (string)GetValue(ordinal);
    public override decimal GetDecimal(int ordinal) => (decimal)GetValue(ordinal);
    public override DateTime GetDateTime(int ordinal) => (DateTime)GetValue(ordinal);
    public override int GetInt32(int ordinal) => (int)GetValue(ordinal);
    public override bool GetBoolean(int ordinal) => (bool)GetValue(ordinal);
    public override byte GetByte(int ordinal) => (byte)GetValue(ordinal);
    public override char GetChar(int ordinal) => (char)GetValue(ordinal);
    public override double GetDouble(int ordinal) => (double)GetValue(ordinal);
    public override float GetFloat(int ordinal) => (float)GetValue(ordinal);
    public override Guid GetGuid(int ordinal) => (Guid)GetValue(ordinal);
    public override short GetInt16(int ordinal) => (short)GetValue(ordinal);
    public override long GetInt64(int ordinal) => (long)GetValue(ordinal);
    public override long GetBytes(int ordinal, long offset, byte[]? buffer, int bufferOffset, int length) =>
        throw new NotSupportedException();
    public override long GetChars(int ordinal, long offset, char[]? buffer, int bufferOffset, int length) =>
        throw new NotSupportedException();
    public override IEnumerator GetEnumerator() => throw new NotSupportedException();
}

/// <summary>Today's shape. Every cell costs a hash insert on the way in and a hash lookup on the way
/// out, on top of one dictionary per row.</summary>
public sealed class DictionaryReader : RepresentationReader
{
    private readonly int _rowCount;
    private readonly Dictionary<string, string> _sourceByTarget;
    private Dictionary<string, object?> _current = null!;
    private int _row = -1;

    public DictionaryReader(int rowCount, int columnCount) : base(columnCount)
    {
        _rowCount = rowCount;
        _sourceByTarget = Enumerable.Range(0, columnCount)
            .ToDictionary(BenchmarkSchema.NameOf, BenchmarkSchema.NameOf);
    }

    public override bool Read()
    {
        if (++_row >= _rowCount)
            return false;

        _current = new Dictionary<string, object?>(ColumnCount);
        for (var c = 0; c < ColumnCount; c++)
            _current[BenchmarkSchema.NameOf(c)] = BenchmarkSchema.KindOf(c) switch
            {
                ColumnKind.Int => BenchmarkSchema.IntValue(_row, c),
                ColumnKind.Money => BenchmarkSchema.MoneyValue(_row, c),
                ColumnKind.Timestamp => BenchmarkSchema.TimestampValue(_row),
                _ => BenchmarkSchema.SharedText,
            };
        return true;
    }

    public override object GetValue(int ordinal) =>
        _current[_sourceByTarget[BenchmarkSchema.NameOf(ordinal)]]!;
}

/// <summary>Positional array per row; the row buffer itself is reused, so only the boxes are garbage.</summary>
public sealed class RowArrayReader(int rowCount, int columnCount) : RepresentationReader(columnCount)
{
    private readonly object?[] _current = new object?[columnCount];
    private int _row = -1;

    public override bool Read()
    {
        if (++_row >= rowCount)
            return false;

        for (var c = 0; c < ColumnCount; c++)
            _current[c] = BenchmarkSchema.KindOf(c) switch
            {
                ColumnKind.Int => BenchmarkSchema.IntValue(_row, c),
                ColumnKind.Money => BenchmarkSchema.MoneyValue(_row, c),
                ColumnKind.Timestamp => BenchmarkSchema.TimestampValue(_row),
                _ => BenchmarkSchema.SharedText,
            };
        return true;
    }

    public override object GetValue(int ordinal) => _current[ordinal]!;
}

/// <summary>
/// Columnar batches over <see cref="ArrayPool{T}"/>-rented typed arrays. Buffers are rented once and
/// reused for every batch, so steady-state array allocation is zero; values are written and read
/// unboxed through the typed accessors, and boxed only when a consumer insists on
/// <see cref="GetValue"/> — one cell at a time.
/// </summary>
public sealed class ColumnarReader : RepresentationReader
{
    private readonly int _rowCount, _batchSize;
    private readonly int[]?[] _ints;
    private readonly decimal[]?[] _money;
    private readonly DateTime[]?[] _timestamps;
    private readonly string[]?[] _text;
    private int _rowsProduced, _batchCount, _cursor = -1;

    public ColumnarReader(int rowCount, int columnCount, int batchSize) : base(columnCount)
    {
        (_rowCount, _batchSize) = (rowCount, batchSize);
        _ints = new int[columnCount][];
        _money = new decimal[columnCount][];
        _timestamps = new DateTime[columnCount][];
        _text = new string[columnCount][];

        for (var c = 0; c < columnCount; c++)
        {
            switch (BenchmarkSchema.KindOf(c))
            {
                case ColumnKind.Int: _ints[c] = ArrayPool<int>.Shared.Rent(batchSize); break;
                case ColumnKind.Money: _money[c] = ArrayPool<decimal>.Shared.Rent(batchSize); break;
                case ColumnKind.Timestamp: _timestamps[c] = ArrayPool<DateTime>.Shared.Rent(batchSize); break;
                default: _text[c] = ArrayPool<string>.Shared.Rent(batchSize); break;
            }
        }
    }

    private void FillBatch()
    {
        _batchCount = Math.Min(_batchSize, _rowCount - _rowsProduced);
        for (var r = 0; r < _batchCount; r++)
        {
            var row = _rowsProduced + r;
            for (var c = 0; c < ColumnCount; c++)
            {
                switch (BenchmarkSchema.KindOf(c))
                {
                    case ColumnKind.Int: _ints[c]![r] = BenchmarkSchema.IntValue(row, c); break;
                    case ColumnKind.Money: _money[c]![r] = BenchmarkSchema.MoneyValue(row, c); break;
                    case ColumnKind.Timestamp: _timestamps[c]![r] = BenchmarkSchema.TimestampValue(row); break;
                    default: _text[c]![r] = BenchmarkSchema.SharedText; break;
                }
            }
        }

        _rowsProduced += _batchCount;
        _cursor = -1;
    }

    public override bool Read()
    {
        if (_cursor + 1 >= _batchCount)
        {
            if (_rowsProduced >= _rowCount)
                return false;
            FillBatch();
            if (_batchCount == 0)
                return false;
        }

        _cursor++;
        return true;
    }

    public override int GetInt32(int ordinal) => _ints[ordinal]![_cursor];
    public override decimal GetDecimal(int ordinal) => _money[ordinal]![_cursor];
    public override DateTime GetDateTime(int ordinal) => _timestamps[ordinal]![_cursor];
    public override string GetString(int ordinal) => _text[ordinal]![_cursor];

    /// <summary>The only boxing site, reached only when the consumer cannot take a typed value.</summary>
    public override object GetValue(int ordinal) => BenchmarkSchema.KindOf(ordinal) switch
    {
        ColumnKind.Int => _ints[ordinal]![_cursor],
        ColumnKind.Money => _money[ordinal]![_cursor],
        ColumnKind.Timestamp => _timestamps[ordinal]![_cursor],
        _ => _text[ordinal]![_cursor],
    };

    protected override void Dispose(bool disposing)
    {
        for (var c = 0; c < ColumnCount; c++)
        {
            if (_ints[c] is { } ints) ArrayPool<int>.Shared.Return(ints);
            if (_money[c] is { } money) ArrayPool<decimal>.Shared.Return(money);
            if (_timestamps[c] is { } stamps) ArrayPool<DateTime>.Shared.Return(stamps);
            if (_text[c] is { } text) ArrayPool<string>.Shared.Return(text, clearArray: true);
        }

        base.Dispose(disposing);
    }
}
