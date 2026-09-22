using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.RegularExpressions;

namespace DbDataSync.Drivers.Jdbc.Imported;

// Adapted from ClrKernel.Database.Provider.Jdbc (Apache-2.0, github.com/ClrKernel/ClrKernel,
// src/ClrKernel.Database.Provider.Jdbc/JdbcCommand.cs) — phase 165V. The imported original executed
// everything through java.sql.Statement and had no parameter support at all (DbParameterCollection and
// Prepare() both threw NotSupportedException/NotImplementedException) — "pass fully-formed SQL", per its
// own doc comment.
//
// This is the phase's real piece of new work: a PreparedStatement-backed path for when parameters are
// present, and the translation a JDBC PreparedStatement needs that no other engine in this repo does.
// WatermarkReader/GenericValueBinder render a *named* marker into CommandText (JdbcDialect.
// ParameterReference emits "@name", matching every other dialect's look) and add parameters to
// DbParameterCollection by name, in an order that does not reliably match the order their markers appear
// in the rendered SQL text — every other provider here (SqlClient, Npgsql, MySqlConnector, Oracle's)
// binds by name at the provider layer, so the add order never had to match text order until now. A JDBC
// PreparedStatement has only ordinal "?" placeholders in strict left-to-right text order, so this class
// does the name→position translation itself: scan CommandText for each parameter's own "@name" marker,
// left to right, replace with "?", and bind positionally in that scan order — the same shape .NET's own
// OdbcCommand already uses for the identical problem (ODBC is likewise ordinal-"?"-only).
internal sealed partial class JdbcCommand : DbCommand
{
    private readonly JdbcConnection _connection;
    private readonly JdbcParameterCollection _parameters = new();
    private java.sql.Statement? _openStatement;
    private int _commandTimeoutSeconds;

    public JdbcCommand(JdbcConnection connection)
    {
        _connection = connection;
    }

    [AllowNull] public override string CommandText { get; set; } = "";
    public override CommandType CommandType { get; set; } = CommandType.Text;
    protected override DbConnection? DbConnection { get => _connection; set => throw new NotImplementedException(); }

    public override int CommandTimeout
    {
        get => _commandTimeoutSeconds;
        set
        {
            _commandTimeoutSeconds = value;
            if (_openStatement is not null)
                _openStatement.setQueryTimeout(value);
        }
    }

    // Out of scope: writers (which need transactions) are not part of this reader-only phase.
    protected override DbTransaction? DbTransaction { get => null; set => throw new NotImplementedException(); }
    public override UpdateRowSource UpdatedRowSource { get => UpdateRowSource.None; set { } }
    public override bool DesignTimeVisible { get; set; }
    protected override DbParameterCollection DbParameterCollection => _parameters;

    protected override DbParameter CreateDbParameter() => new JdbcParameter();

    public override void Prepare() { /* every execution already prepares when it has parameters to bind */ }

    public override void Cancel() => _openStatement?.cancel();

    public override int ExecuteNonQuery()
    {
        var (statement, sql) = OpenStatement();
        return statement switch
        {
            java.sql.PreparedStatement prepared => prepared.executeUpdate(),
            _ => statement.executeUpdate(sql),
        };
    }

    protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior)
    {
        if (string.IsNullOrWhiteSpace(CommandText))
            throw new ArgumentException("CommandText must be set before executing the command.");
        if (behavior.HasFlag(CommandBehavior.SchemaOnly)
            || behavior.HasFlag(CommandBehavior.KeyInfo)
            || behavior.HasFlag(CommandBehavior.SequentialAccess))
            throw new NotImplementedException("Unsupported behavior: SchemaOnly, KeyInfo, SequentialAccess.");

        var (statement, sql) = OpenStatement();
        if (behavior.HasFlag(CommandBehavior.SingleRow))
            statement.setMaxRows(1);

        var resultSet = statement switch
        {
            java.sql.PreparedStatement prepared => prepared.executeQuery(),
            _ => statement.executeQuery(CommandType switch
            {
                CommandType.TableDirect => $"select * from {sql}",
                _ => sql,
            }),
        };
        return new JdbcDataReader(resultSet);
    }

    public override object? ExecuteScalar()
    {
        using var reader = ExecuteReader(CommandBehavior.Default);
        return reader.Read() ? reader.GetValue(0) : null;
    }

    /// <summary>
    /// Opens (or reuses) whichever <c>java.sql.Statement</c> this execution needs: a plain
    /// <c>Statement</c> when there are no parameters to bind (so <c>BatchReloadReader</c>, which binds
    /// nothing, is entirely unaffected by any of this), or a <c>PreparedStatement</c> built from the
    /// translated SQL when there are.
    /// </summary>
    private (java.sql.Statement Statement, string Sql) OpenStatement()
    {
        _openStatement?.close();

        if (_parameters.Count == 0)
        {
            var statement = _connection.JavaSqlConnection.createStatement();
            statement.setQueryTimeout(_commandTimeoutSeconds);
            _openStatement = statement;
            return (statement, CommandText);
        }

        var (sql, ordered) = TranslateParameters(CommandText);
        var prepared = _connection.JavaSqlConnection.prepareStatement(sql);
        prepared.setQueryTimeout(_commandTimeoutSeconds);
        for (var position = 0; position < ordered.Count; position++)
            Bind(prepared, position + 1, ordered[position]);
        _openStatement = prepared;
        return (prepared, sql);
    }

    [GeneratedRegex(@"@(\w+)")]
    private static partial Regex ParameterMarker();

    /// <summary>
    /// Replaces every <c>@name</c> marker in <paramref name="sql"/> with a plain <c>?</c>, left to right,
    /// and returns the parameters in that same order — the order a <c>PreparedStatement</c>'s ordinal
    /// binding needs, which is not necessarily <see cref="_parameters"/>'s own add order.
    /// </summary>
    private (string Sql, List<JdbcParameter> Ordered) TranslateParameters(string sql)
    {
        var ordered = new List<JdbcParameter>();
        var translated = ParameterMarker().Replace(sql, match =>
        {
            var name = match.Groups[1].Value;
            var index = _parameters.IndexOf(name);
            if (index < 0)
                throw new InvalidOperationException(
                    $"CommandText references parameter '@{name}' with no matching entry in Parameters.");
            ordered.Add((JdbcParameter)_parameters[index]);
            return "?";
        });
        return (translated, ordered);
    }

    private static void Bind(java.sql.PreparedStatement statement, int position, JdbcParameter parameter)
    {
        if (parameter.Value is null or DBNull)
        {
            statement.setNull(position, JavaSqlType(parameter.DbType));
            return;
        }

        switch (parameter.DbType)
        {
            case DbType.Boolean:
                statement.setBoolean(position, (bool)parameter.Value);
                break;
            case DbType.SByte:
                statement.setByte(position, unchecked((byte)(sbyte)parameter.Value));
                break;
            case DbType.Int16:
                statement.setShort(position, (short)parameter.Value);
                break;
            case DbType.Int32:
                statement.setInt(position, (int)parameter.Value);
                break;
            case DbType.Int64:
                statement.setLong(position, (long)parameter.Value);
                break;
            case DbType.Single:
                statement.setFloat(position, (float)parameter.Value);
                break;
            case DbType.Double:
                statement.setDouble(position, (double)parameter.Value);
                break;
            case DbType.Decimal or DbType.Currency or DbType.VarNumeric:
                statement.setBigDecimal(position, ToJavaBigDecimal((decimal)parameter.Value));
                break;
            case DbType.Date:
                statement.setDate(position, java.sql.Date.valueOf(((DateOnly)parameter.Value).ToString("yyyy-MM-dd")));
                break;
            case DbType.Time:
                statement.setTime(position, java.sql.Time.valueOf(TimeOnly.FromTimeSpan((TimeSpan)parameter.Value).ToString("HH:mm:ss")));
                break;
            case DbType.DateTime or DbType.DateTime2:
                statement.setTimestamp(position, ToJavaTimestamp((DateTime)parameter.Value));
                break;
            case DbType.DateTimeOffset:
                statement.setTimestamp(position, ToJavaTimestamp(((DateTimeOffset)parameter.Value).UtcDateTime));
                break;
            case DbType.Binary:
                statement.setBytes(position, (byte[])parameter.Value);
                break;
            case DbType.Guid:
                statement.setString(position, parameter.Value.ToString());
                break;
            default:
                statement.setString(position, Convert.ToString(parameter.Value, CultureInfo.InvariantCulture));
                break;
        }
    }

    private static java.math.BigDecimal ToJavaBigDecimal(decimal value) =>
        new(value.ToString(CultureInfo.InvariantCulture));

    private static java.sql.Timestamp ToJavaTimestamp(DateTime value) =>
        java.sql.Timestamp.valueOf(value.ToString("yyyy-MM-dd HH:mm:ss.fffffff", CultureInfo.InvariantCulture));

    private static int JavaSqlType(DbType dbType) => dbType switch
    {
        DbType.Boolean => java.sql.Types.BOOLEAN,
        DbType.SByte or DbType.Byte => java.sql.Types.TINYINT,
        DbType.Int16 or DbType.UInt16 => java.sql.Types.SMALLINT,
        DbType.Int32 or DbType.UInt32 => java.sql.Types.INTEGER,
        DbType.Int64 or DbType.UInt64 => java.sql.Types.BIGINT,
        DbType.Single => java.sql.Types.REAL,
        DbType.Double => java.sql.Types.DOUBLE,
        DbType.Decimal or DbType.Currency or DbType.VarNumeric => java.sql.Types.DECIMAL,
        DbType.Date => java.sql.Types.DATE,
        DbType.Time => java.sql.Types.TIME,
        DbType.DateTime or DbType.DateTime2 => java.sql.Types.TIMESTAMP,
        DbType.DateTimeOffset => java.sql.Types.TIMESTAMP_WITH_TIMEZONE,
        DbType.Binary => java.sql.Types.VARBINARY,
        _ => java.sql.Types.VARCHAR,
    };

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            _openStatement?.close();
        _openStatement = null;
        base.Dispose(disposing);
    }
}
