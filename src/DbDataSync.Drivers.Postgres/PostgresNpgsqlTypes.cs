using DbDataSync.Drivers.Generic;
using NpgsqlTypes;

namespace DbDataSync.Drivers.Postgres;

/// <summary>
/// A Postgres column's declared type, as an <see cref="NpgsqlDbType"/>.
/// <para>
/// Two callers need this and they need it for the same reason, which is why it is one table rather
/// than two: <see cref="PostgresValueBinding"/> types a segment bound so the comparison happens on the
/// bound's side rather than the column's, and <see cref="PgCopyStagingProvider"/> types every cell it
/// writes because binary <c>COPY</c> has no server-side coercion at all — the wire format has to match
/// the column exactly or the row is rejected.
/// </para>
/// <para>
/// Both the <c>information_schema</c> spellings (<c>integer</c>, <c>character varying</c>) and the
/// internal ones (<c>int4</c>, <c>varchar</c>), because a column's type reaches here from either
/// depending on which catalog query produced it.
/// </para>
/// </summary>
internal static class PostgresNpgsqlTypes
{
    /// <summary>
    /// <paramref name="nativeType"/> may carry a length or precision (<c>numeric(18,2)</c>,
    /// <c>character varying(50)</c>); only the base name decides the type.
    /// </summary>
    public static NpgsqlDbType Of(string nativeType) => SqlTypeName.BaseOf(nativeType) switch
    {
        "smallint" or "int2" => NpgsqlDbType.Smallint,
        "integer" or "int" or "int4" or "serial" => NpgsqlDbType.Integer,
        "bigint" or "int8" or "bigserial" => NpgsqlDbType.Bigint,
        "boolean" or "bool" => NpgsqlDbType.Boolean,
        "numeric" or "decimal" or "money" => NpgsqlDbType.Numeric,
        "double precision" or "float8" => NpgsqlDbType.Double,
        "real" or "float4" => NpgsqlDbType.Real,
        "date" => NpgsqlDbType.Date,
        "timestamp" or "timestamp without time zone" => NpgsqlDbType.Timestamp,
        "timestamptz" or "timestamp with time zone" => NpgsqlDbType.TimestampTz,
        "time" or "time without time zone" => NpgsqlDbType.Time,
        "uuid" => NpgsqlDbType.Uuid,
        "bytea" => NpgsqlDbType.Bytea,
        "character" or "bpchar" or "char" => NpgsqlDbType.Char,
        "character varying" or "varchar" => NpgsqlDbType.Varchar,
        _ => NpgsqlDbType.Text,
    };
}
