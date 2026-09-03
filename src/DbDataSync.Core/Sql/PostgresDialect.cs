using System.Data.Common;

namespace DbDataSync.Core.Sql;

/// <summary>
/// PostgreSQL's answers to the mechanical variations in <see cref="SqlDialect"/>.
/// <para>
/// Two of them are not mechanical at all and are worth naming: a Postgres connection cannot change
/// database, and an <c>INSERT</c> supplying a <c>GENERATED ALWAYS AS IDENTITY</c> column needs
/// <c>OVERRIDING SYSTEM VALUE</c> written into the statement rather than a session flag around it.
/// Both are exactly the hooks phase 18 defined, which is the evidence they were the right seams.
/// </para>
/// </summary>
public sealed class PostgresDialect : SqlDialect
{
    public static PostgresDialect Instance { get; } = new();

    private PostgresDialect() { }

    public override string QuoteIdentifier(string identifier) => $"\"{identifier.Replace("\"", "\"\"")}\"";

    public override string ParameterReference(string name) => $"@{name}";

    /// <summary>Postgres allows 65535 bound parameters, so batches are far larger than SQL Server's.</summary>
    public override int MaxParametersPerStatement => 65535;

    public override string OperationMarkerColumnType => "char(1)";

    /// <summary>
    /// A Postgres connection is bound to one database for its lifetime — <c>ChangeDatabase</c> would
    /// have to close and reopen it, which silently discards the transaction and any open cursor the
    /// caller is streaming from. So this validates instead: the connection is already pointed at its
    /// database by the connection string, and a mapping asking for a different one is a configuration
    /// error worth saying out loud rather than a reconnect worth performing.
    /// </summary>
    public override Task UseDatabaseAsync(DbConnection connection, string database, CancellationToken cancellationToken)
    {
        if (!string.Equals(connection.Database, database, StringComparison.Ordinal))
            throw new InvalidOperationException(
                $"This connection is bound to database '{connection.Database}', but '{database}' was requested. " +
                "A PostgreSQL connection cannot change database — configure a separate DbDataSync connection " +
                "for each database.");

        return Task.CompletedTask;
    }

    /// <summary>
    /// <c>OVERRIDING SYSTEM VALUE</c> belongs to a column generated <c>ALWAYS</c>; a <c>BY DEFAULT</c>
    /// identity and an old-style <c>serial</c> accept an explicit value without it, which is what
    /// <see cref="PostgresCatalog"/> encodes when it sets <c>IsIdentity</c>.
    /// </summary>
    public override string RenderInsertInto(string qualifiedTable, string columnList, bool overrideGenerated) =>
        overrideGenerated
            ? $"INSERT INTO {qualifiedTable} ({columnList}) OVERRIDING SYSTEM VALUE"
            : $"INSERT INTO {qualifiedTable} ({columnList})";

    /// <summary>
    /// A pass-through: the override is part of the statement (see <see cref="RenderInsertInto"/>), not
    /// a setting around it. Which is exactly why phase 18 made this hook "run this write" rather than
    /// "give me a clause" — the two engines put the same intent in different places.
    /// </summary>
    public override Task<T> WriteWithGeneratedColumnOverrideAsync<T>(
        DbConnection connection,
        DbTransaction? transaction,
        string qualifiedTable,
        bool overrideRequired,
        Func<Task<T>> write,
        CancellationToken cancellationToken) => write();

    public override BucketableKind ClassifyForBucketing(string baseTypeName) => baseTypeName switch
    {
        "int2" or "int4" or "int8" or "serial" or "bigserial" or "smallserial" => BucketableKind.Integral,
        "float4" or "float8" or "money" => BucketableKind.Numeric,
        "timestamp without time zone" or "timestamp with time zone" => BucketableKind.DateTime,
        "timestamptz" => BucketableKind.DateTimeOffset,
        _ => base.ClassifyForBucketing(baseTypeName),
    };

    /// <summary>See the type-mapping table in phase 25 §2. Postgres has one string type family and no
    /// notion of "narrow" vs "wide" characters, so <see cref="CanonicalType.IsUnicode"/> is always true
    /// here — a round trip through Postgres never narrows a string. Array and user-defined types fall
    /// through to <see cref="CanonicalTypeKind.Unmappable"/>, per the doc's "array types → Unmappable".</summary>
    public override CanonicalType ToCanonicalType(string nativeType)
    {
        var (baseName, args) = CanonicalTypeSpec.Parse(nativeType);
        return baseName switch
        {
            "boolean" or "bool" => Simple(CanonicalTypeKind.Boolean),
            "smallint" or "int2" or "smallserial" => Simple(CanonicalTypeKind.Int16),
            "integer" or "int4" or "serial" => Simple(CanonicalTypeKind.Int32),
            "bigint" or "int8" or "bigserial" => Simple(CanonicalTypeKind.Int64),

            "numeric" or "decimal" => new CanonicalType(
                CanonicalTypeKind.Decimal, null, CanonicalTypeSpec.IntAt(args, 0, 18), CanonicalTypeSpec.IntAt(args, 1, 0), false, false),

            "money" => new CanonicalType(CanonicalTypeKind.Decimal, null, 19, 4, false, false,
                SourceNote: "PostgreSQL 'money' carries currency semantics that a generic decimal does not; only its scale survives."),

            "real" or "float4" => Simple(CanonicalTypeKind.Float),
            "double precision" or "float8" => Simple(CanonicalTypeKind.Double),

            "character varying" or "varchar" =>
                new CanonicalType(CanonicalTypeKind.String, CanonicalTypeSpec.IntAt(args, 0, null), null, null, true, false),
            "character" or "char" or "bpchar" =>
                new CanonicalType(CanonicalTypeKind.String, CanonicalTypeSpec.IntAt(args, 0, 1), null, null, true, false),
            "text" => new CanonicalType(CanonicalTypeKind.String, null, null, null, true, true),

            "bytea" => new CanonicalType(CanonicalTypeKind.Binary, null, null, null, false, true),

            "date" => Simple(CanonicalTypeKind.Date),
            "time without time zone" or "time" => new CanonicalType(CanonicalTypeKind.Time, null, null, CanonicalTypeSpec.IntAt(args, 0, 6), false, false),
            "timestamp without time zone" or "timestamp" =>
                new CanonicalType(CanonicalTypeKind.Timestamp, null, null, CanonicalTypeSpec.IntAt(args, 0, 6), false, false),
            "timestamp with time zone" or "timestamptz" =>
                new CanonicalType(CanonicalTypeKind.TimestampTz, null, null, CanonicalTypeSpec.IntAt(args, 0, 6), false, false),

            "uuid" => Simple(CanonicalTypeKind.Guid),
            "json" => Simple(CanonicalTypeKind.Json),
            "jsonb" => Simple(CanonicalTypeKind.Json),
            "xml" => Simple(CanonicalTypeKind.Xml),

            // "time with time zone" is a Postgres-only oddity with no faithful cross-engine target;
            // arrays and user-defined (enum) types are the doc's "array types → Unmappable" — none of
            // these get a guessed rendering.
            _ => Simple(CanonicalTypeKind.Unmappable),
        };

        static CanonicalType Simple(CanonicalTypeKind kind) => new(kind, null, null, null, false, false);
    }

    public override RenderedColumnType RenderColumnType(CanonicalType type) => type.Kind switch
    {
        CanonicalTypeKind.Boolean => Faithful("boolean", type),
        // Postgres has no 1-byte integer type — the documented widening.
        CanonicalTypeKind.Int8 => new RenderedColumnType(
            "smallint", Combine(type, "Widened; PostgreSQL has no 1-byte integer type.")),
        CanonicalTypeKind.Int16 => Faithful("smallint", type),
        CanonicalTypeKind.Int32 => Faithful("integer", type),
        CanonicalTypeKind.Int64 => Faithful("bigint", type),
        CanonicalTypeKind.Decimal => Faithful($"numeric({type.Precision ?? 18},{type.Scale ?? 0})", type),
        CanonicalTypeKind.Float => Faithful("real", type),
        CanonicalTypeKind.Double => Faithful("double precision", type),

        CanonicalTypeKind.String when type.IsMax =>
            new RenderedColumnType("text", Combine(type, "No length bound at the target.")),
        CanonicalTypeKind.String => Faithful($"varchar({type.Length ?? 255})", type),

        CanonicalTypeKind.Binary => Faithful("bytea", type.IsMax || type.Length is null
            ? type
            : type with { SourceNote = Combine(type, "PostgreSQL bytea has no length constraint; it is not enforced at the target.") }),

        CanonicalTypeKind.Date => Faithful("date", type),
        CanonicalTypeKind.Time => Faithful($"time({Math.Min(type.Scale ?? 6, 6)})", type),

        CanonicalTypeKind.Timestamp => (type.Scale ?? 6) > 6
            ? new RenderedColumnType("timestamp(6)", Combine(type,
                "One digit of sub-second precision lost (SQL Server datetime2 supports 7 digits; PostgreSQL timestamp supports 6)."))
            : Faithful($"timestamp({type.Scale ?? 6})", type),

        CanonicalTypeKind.TimestampTz => Faithful("timestamptz", type),
        CanonicalTypeKind.Guid => Faithful("uuid", type),
        CanonicalTypeKind.Xml => Faithful("xml", type),
        CanonicalTypeKind.Json => Faithful("jsonb", type),

        CanonicalTypeKind.Unmappable => throw new InvalidOperationException(
            "RenderColumnType must never be called for Unmappable — the caller checks CanonicalType.Kind " +
            "first and reports the plan as Unsupported instead."),

        _ => throw new ArgumentOutOfRangeException(nameof(type), type.Kind, "Unknown canonical type kind."),
    };

    private static RenderedColumnType Faithful(string sql, CanonicalType type) => new(sql, type.SourceNote);

    private static string Combine(CanonicalType type, string renderNote) =>
        type.SourceNote is null ? renderNote : $"{type.SourceNote} {renderNote}";
}
