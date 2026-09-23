using System.Collections;
using System.Data;
using System.Data.Common;
using JdbcTypes = java.sql.Types;

namespace DbDataSync.Drivers.Jdbc.Ado;

// Adapted from ClrKernel.Database.Provider.Jdbc (Apache-2.0, github.com/ClrKernel/ClrKernel,
// src/ClrKernel.Database.Provider.Jdbc/JdbcDataReader.cs) — phase 165V. Adapts a java.sql.ResultSet to
// DbDataReader.
//
// Fixed here (phase 165V's own "Open question 4", found by reading the imported source before running
// it, and confirmed by a throwaway probe explicitly checking wasNull() where these getters do not):
// GetDateTime/GetDecimal called ToDateTime/ToDecimal directly on a possibly-null Java reference with no
// null check, so a NULL DATE/NUMERIC column read through the typed getter raised an unhandled
// NullPointerException from IKVM's side instead of the InvalidCastException ADO.NET convention expects
// (compare System.Data.SqlClient/Npgsql, which both throw InvalidCastException("Data is Null...") for
// exactly this). GetValue/this[i] (what every reader in this repo actually calls) were already correct —
// JdbcResultToClrObject already checked wasNull() — so this fix is for correctness under direct use of
// the typed getters, not a change to the path DbDataSync's own readers take.
internal sealed class JdbcDataReader : DbDataReader
{
    private java.sql.ResultSet? _resultSet;
    private readonly string[] _fields;
    private readonly Type[] _types;
    private readonly string[] _typeNames;
    private readonly int[] _jdbcTypes;
    private DataTable? _schemaTable;

    public JdbcDataReader(java.sql.ResultSet resultSet)
    {
        _resultSet = resultSet;
        var metadata = resultSet.getMetaData();
        var columnCount = metadata.getColumnCount();
        _fields = new string[columnCount];
        _types = new Type[columnCount];
        _typeNames = new string[columnCount];
        _jdbcTypes = new int[columnCount];
        for (var i = 0; i < columnCount; i++)
        {
            var columnIndex = i + 1;
            var columnJdbcType = metadata.getColumnType(columnIndex);
            _fields[i] = metadata.getColumnName(columnIndex);
            _types[i] = JdbcTypeToClrType(columnJdbcType);
            _typeNames[i] = metadata.getColumnTypeName(columnIndex);
            _jdbcTypes[i] = columnJdbcType;
        }
    }

    private java.sql.ResultSet ResultSet => _resultSet ?? throw new ObjectDisposedException(nameof(JdbcDataReader));

    public override DataTable GetSchemaTable()
    {
        if (_schemaTable != null)
            return _schemaTable;

        _schemaTable = new DataTable
        {
            Columns =
            {
                { "ColumnName", typeof(string) },
                { "ColumnOrdinal", typeof(int) },
                { "BaseColumnName", typeof(string) },
                { "DataType", typeof(Type) },
                { "ProviderType", typeof(Type) },
                { "AllowDBNull", typeof(bool) },
                { "ColumnSize", typeof(int) },
            },
        };
        for (var i = 0; i < _fields.Length; i++)
        {
            var row = _schemaTable.Rows.Add();
            var name = string.IsNullOrWhiteSpace(_fields[i]) ? "Column " + i : _fields[i];
            var type = _types[i];
            row[0] = name;
            row[1] = i;
            row[2] = name;
            row[3] = type;
            row[4] = type;
            row[5] = true;
            row[6] = 0;
        }
        return _schemaTable;
    }

    public override int FieldCount => _fields.Length;
    public override bool NextResult() => false;
    public override bool Read() => ResultSet.next();
    public override string GetName(int i) => _fields[i];
    public override int GetOrdinal(string name) => Array.IndexOf(_fields, name);
    public override bool IsDBNull(int i) => GetValue(i) == DBNull.Value;
    public override void Close() => _resultSet?.close();
    public override object GetValue(int i) => JdbcResultToClrObject(i);
    public override IEnumerator GetEnumerator() => new DbEnumerator(this);

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            Close();
        _resultSet = null;
        base.Dispose(disposing);
    }

    public override object this[int i] => GetValue(i);
    public override object this[string name] => GetValue(Array.IndexOf(_fields, name));
    public override Type GetFieldType(int i) => _types[i];
    public override string GetDataTypeName(int i) => _typeNames[i];
    public override bool GetBoolean(int i) => ResultSet.getBoolean(i + 1);
    public override byte GetByte(int i) => ResultSet.getByte(i + 1);
    public override long GetBytes(int i, long fieldOffset, byte[]? buffer, int bufferoffset, int length) => throw new NotImplementedException();
    public override char GetChar(int i) => ResultSet.getString(i + 1)[0];
    public override long GetChars(int i, long fieldoffset, char[]? buffer, int bufferoffset, int length) => throw new NotImplementedException();

    public override DateTime GetDateTime(int i) =>
        ResultSet.getTimestamp(i + 1) is { } value ? ToDateTime(value) : throw NullCast(i);

    public override decimal GetDecimal(int i) =>
        ResultSet.getBigDecimal(i + 1) is { } value ? ToDecimal(value) : throw NullCast(i);

    public override double GetDouble(int i) => ResultSet.getDouble(i + 1);
    public override float GetFloat(int i) => ResultSet.getFloat(i + 1);
    public override short GetInt16(int i) => ResultSet.getShort(i + 1);
    public override int GetInt32(int i) => ResultSet.getInt(i + 1);
    public override long GetInt64(int i) => ResultSet.getLong(i + 1);
    public override string GetString(int i) => ResultSet.getString(i + 1) ?? throw NullCast(i);
    public override Guid GetGuid(int i) => Guid.Parse(ResultSet.getString(i + 1) ?? throw NullCast(i));

    private static InvalidCastException NullCast(int ordinal) =>
        new($"Data is Null. This method or property cannot be called on column {ordinal} for a Null value.");

    public override int GetValues(object[] values)
    {
        ArgumentNullException.ThrowIfNull(values);
        var columns = Math.Min(values.Length, _fields.Length);
        for (var i = 0; i < columns; i++)
            values[i] = GetValue(i);
        return columns;
    }

    public override int Depth => 0;
    public override int RecordsAffected => -1;
    public override bool IsClosed => _resultSet is null || _resultSet.isClosed();
    public override bool HasRows => throw new NotImplementedException();

    // java.sql.Types → CLR type (https://docs.oracle.com/javase/8/docs/api/java/sql/Types.html)
    private static Type JdbcTypeToClrType(int type) =>
        type switch
        {
            JdbcTypes.BIT => typeof(bool),
            JdbcTypes.TINYINT => typeof(byte),
            JdbcTypes.SMALLINT => typeof(short),
            JdbcTypes.INTEGER => typeof(int),
            JdbcTypes.BIGINT => typeof(long),
            JdbcTypes.DOUBLE => typeof(double),
            JdbcTypes.FLOAT => typeof(float),
            JdbcTypes.REAL => typeof(float),
            JdbcTypes.CHAR => typeof(string),
            JdbcTypes.NCHAR => typeof(string),
            JdbcTypes.LONGNVARCHAR => typeof(string),
            JdbcTypes.LONGVARCHAR => typeof(string),
            JdbcTypes.NVARCHAR => typeof(string),
            JdbcTypes.VARCHAR => typeof(string),
            JdbcTypes.DECIMAL => typeof(decimal),
            JdbcTypes.NUMERIC => typeof(decimal),
            JdbcTypes.DATE => typeof(DateOnly),
            JdbcTypes.TIME => typeof(TimeOnly),
            JdbcTypes.TIMESTAMP => typeof(DateTime),
            JdbcTypes.VARBINARY => typeof(byte[]),
            JdbcTypes.BINARY => typeof(byte[]),
            _ => typeof(object),
        };

    private object JdbcResultToClrObject(int i)
    {
        var type = _jdbcTypes[i];
        var columnIndex = i + 1;
        var resultSet = ResultSet;

        object? ReadDate() => resultSet.getDate(columnIndex) is { } value ? DateOnly.FromDateTime(ToDateTime(value)) : null;
        object? ReadTime() => resultSet.getTime(columnIndex) is { } value ? TimeOnly.FromDateTime(ToDateTime(value)) : null;
        object? ReadTimeStamp() => resultSet.getTimestamp(columnIndex) is { } value ? ToDateTime(value) : null;
        object? ReadDecimal() => resultSet.getBigDecimal(columnIndex) is { } value ? ToDecimal(value) : null;

        object? result = type switch
        {
            JdbcTypes.BIT => resultSet.getBoolean(columnIndex),
            JdbcTypes.TINYINT => resultSet.getByte(columnIndex),
            JdbcTypes.SMALLINT => resultSet.getShort(columnIndex),
            JdbcTypes.INTEGER => resultSet.getInt(columnIndex),
            JdbcTypes.BIGINT => resultSet.getLong(columnIndex),
            JdbcTypes.DOUBLE => resultSet.getDouble(columnIndex),
            JdbcTypes.FLOAT => resultSet.getFloat(columnIndex),
            JdbcTypes.REAL => resultSet.getFloat(columnIndex),
            JdbcTypes.CHAR => resultSet.getString(columnIndex),
            JdbcTypes.NCHAR => resultSet.getString(columnIndex),
            JdbcTypes.LONGNVARCHAR => resultSet.getString(columnIndex),
            JdbcTypes.LONGVARCHAR => resultSet.getString(columnIndex),
            JdbcTypes.NVARCHAR => resultSet.getString(columnIndex),
            JdbcTypes.VARCHAR => resultSet.getString(columnIndex),
            JdbcTypes.DECIMAL => ReadDecimal(),
            JdbcTypes.NUMERIC => ReadDecimal(),
            JdbcTypes.DATE => ReadDate(),
            JdbcTypes.TIME => ReadTime(),
            JdbcTypes.TIMESTAMP => ReadTimeStamp(),
            JdbcTypes.VARBINARY => resultSet.getBytes(columnIndex),
            JdbcTypes.BINARY => resultSet.getBytes(columnIndex),
            _ => resultSet.getObject(columnIndex),
        };

        var wasNull = resultSet.wasNull() || result is null;
        return wasNull ? DBNull.Value : result!;
    }

    // Kind is deliberately Unspecified, not Local: DateTimeOffset.LocalDateTime stamps Kind.Local, but
    // every other engine's DateTime for a "timestamp without time zone" column in this repo carries
    // Unspecified (see GenericValueBinder's own CanonicalTypeKind.Timestamp case) — a real parity bug
    // phase 165V's own comparison-against-Npgsql test caught: same instant, different Kind, which
    // differs under DateTime.ToString("o") (WatermarkValue.Format's own round-trip format) even though
    // the wall-clock value is identical.
    private static DateTime ToDateTime(java.util.Date date) =>
        DateTime.SpecifyKind(DateTimeOffset.FromUnixTimeMilliseconds(date.getTime()).LocalDateTime, DateTimeKind.Unspecified);

    private static decimal ToDecimal(java.math.BigDecimal d) =>
        decimal.Parse(d.toString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture);
}
