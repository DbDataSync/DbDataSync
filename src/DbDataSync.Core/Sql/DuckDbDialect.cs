using System.Data.Common;

namespace DbDataSync.Core.Sql;

/// <summary>
/// DuckDB's answers to the mechanical variations in <see cref="SqlDialect"/>.
/// <para>
/// **Not required by <c>DuckDbQueryReader</c>**, which generates no SQL at all — the operator's query
/// is the statement. It exists because everything *around* a reader asks its driver for a dialect
/// regardless of what the reader does with one: <c>RunExecutor.ResolveDialect</c> builds the
/// watermark key from it, and <c>ProvisioningService</c> translates the *source's* native column types
/// through it to render the target's DDL. A driver that named no dialect would fail on the first pass
/// rather than at configuration time, which is the wrong end of the run to find out.
/// </para>
/// </summary>
public sealed class DuckDbDialect : SqlDialect
{
    public static DuckDbDialect Instance { get; } = new();

    private DuckDbDialect() { }

    public override string QuoteIdentifier(string identifier) => $"\"{identifier.Replace("\"", "\"\"")}\"";

    /// <summary>DuckDB names a prepared parameter with <c>$name</c>; <c>@name</c> is not its syntax.</summary>
    public override string ParameterReference(string name) => $"${name}";

    /// <summary>
    /// Accepts anything and does nothing. <c>ChangeDatabase</c> throws on a DuckDB connection, so the
    /// only question is whether to *validate* the name the way <see cref="PostgresDialect"/> does —
    /// and here the answer is no, for two reasons that both point the same way.
    /// <para>
    /// The first is that <c>connection.Database</c> is not a complete answer to begin with: a DuckDB
    /// connection can <c>ATTACH</c> several databases and a query may legitimately name any of them,
    /// so a mismatch against the one the connection string opened proves nothing.
    /// </para>
    /// <para>
    /// The second is that a query-first source has no database to name in the first place — the rows
    /// come from whatever the query's scanners reach. Config still requires the field
    /// (<c>EndpointResolution.Resolve</c> rejects a mapping whose source database is blank), so it
    /// carries a label rather than a fact, and rejecting a working configuration over the wording of
    /// a label would be this dialect enforcing a rule nothing above it means.
    /// </para>
    /// </summary>
    public override Task UseDatabaseAsync(
        DbConnection connection, string database, CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>DuckDB's default schema, which is what an unqualified name resolves to.</summary>
    public override string QualifyTable(string schema, string table) =>
        base.QualifyTable(string.IsNullOrWhiteSpace(schema) ? "main" : schema, table);

    /// <summary>Plain <c>LIMIT n</c> rather than the ANSI base's <c>FETCH FIRST … ROWS ONLY</c> —
    /// DuckDB definitely supports the former; whether it accepts the latter was not confirmed against a
    /// live instance, and a query preview's own cap (phase 193S, the one caller today) isn't worth
    /// guessing on.</summary>
    public override (string Prefix, string Suffix) RenderRowLimit(int n) => ("", $" LIMIT {n}");

    public override BucketableKind ClassifyForBucketing(string baseTypeName) => baseTypeName switch
    {
        "tinyint" or "smallint" or "integer" or "bigint" or "hugeint"
            or "utinyint" or "usmallint" or "uinteger" or "ubigint" => BucketableKind.Integral,
        "float" or "real" or "double" => BucketableKind.Numeric,
        "timestamp" => BucketableKind.DateTime,
        "timestamptz" or "timestamp with time zone" => BucketableKind.DateTimeOffset,
        _ => base.ClassifyForBucketing(baseTypeName),
    };

    /// <summary>
    /// DuckDB's type names, as <c>information_schema</c> and a result-set schema report them. It has
    /// one string family with no length bound and no narrow/wide distinction, so
    /// <see cref="CanonicalType.IsUnicode"/> is always true and every string is
    /// <see cref="CanonicalType.IsMax"/> unless a length was actually declared. Nested types — LIST,
    /// STRUCT, MAP, UNION — fall through to <see cref="CanonicalTypeKind.Unmappable"/>: there is no
    /// faithful scalar column on any target engine in scope, and guessing one would silently flatten
    /// data.
    /// </summary>
    public override CanonicalType ToCanonicalType(string nativeType)
    {
        var (baseName, args) = CanonicalTypeSpec.Parse(nativeType);
        return baseName switch
        {
            "boolean" or "bool" or "logical" => Simple(CanonicalTypeKind.Boolean),
            "tinyint" or "int1" => Simple(CanonicalTypeKind.Int8),
            "smallint" or "int2" or "short" or "utinyint" => Simple(CanonicalTypeKind.Int16),
            "integer" or "int" or "int4" or "signed" or "usmallint" => Simple(CanonicalTypeKind.Int32),
            "bigint" or "int8" or "long" or "uinteger" => Simple(CanonicalTypeKind.Int64),

            "decimal" or "numeric" => new CanonicalType(
                CanonicalTypeKind.Decimal, null,
                CanonicalTypeSpec.IntAt(args, 0, 18), CanonicalTypeSpec.IntAt(args, 1, 3), false, false),

            "real" or "float4" or "float" => Simple(CanonicalTypeKind.Float),
            "double" or "float8" => Simple(CanonicalTypeKind.Double),

            // Wider than any target engine in scope has. Named rather than dropped to Unmappable: the
            // values that fit still round-trip, and the note is what tells an operator the ones that
            // do not are the ones to look at.
            "hugeint" or "ubigint" or "uhugeint" => new CanonicalType(
                CanonicalTypeKind.Decimal, null, 38, 0, false, false,
                SourceNote: $"DuckDB '{baseName}' is wider than any integer type on the target; values beyond " +
                            "38 digits of precision will not survive."),

            "varchar" or "text" or "string" or "char" or "bpchar" => new CanonicalType(
                CanonicalTypeKind.String, CanonicalTypeSpec.IntAt(args, 0, null), null, null,
                IsUnicode: true, IsMax: args.Count == 0),

            "blob" or "bytea" or "binary" or "varbinary" =>
                new CanonicalType(CanonicalTypeKind.Binary, null, null, null, false, true),

            "date" => Simple(CanonicalTypeKind.Date),
            "time" => Simple(CanonicalTypeKind.Time),
            "timestamp" or "datetime" => new CanonicalType(CanonicalTypeKind.Timestamp, null, null, 6, false, false),
            "timestamptz" or "timestamp with time zone" =>
                new CanonicalType(CanonicalTypeKind.TimestampTz, null, null, 6, false, false),

            "uuid" => Simple(CanonicalTypeKind.Guid),
            "json" => Simple(CanonicalTypeKind.Json),

            _ => Simple(CanonicalTypeKind.Unmappable),
        };

        static CanonicalType Simple(CanonicalTypeKind kind) => new(kind, null, null, null, false, false);
    }

    /// <summary>
    /// Implemented in full even though nothing renders DuckDB DDL today — DuckDB is a source-only
    /// driver this phase, with no writer and no provisioner. Left throwing, the first phase to add one
    /// would find the gap at run time; the mapping is mechanical and short enough that stating it now
    /// is cheaper than the note explaining why it is missing.
    /// </summary>
    public override RenderedColumnType RenderColumnType(CanonicalType type) => type.Kind switch
    {
        CanonicalTypeKind.Boolean => Faithful("BOOLEAN", type),
        CanonicalTypeKind.Int8 => Faithful("TINYINT", type),
        CanonicalTypeKind.Int16 => Faithful("SMALLINT", type),
        CanonicalTypeKind.Int32 => Faithful("INTEGER", type),
        CanonicalTypeKind.Int64 => Faithful("BIGINT", type),
        CanonicalTypeKind.Decimal => Faithful($"DECIMAL({type.Precision ?? 18},{type.Scale ?? 3})", type),
        CanonicalTypeKind.Float => Faithful("FLOAT", type),
        CanonicalTypeKind.Double => Faithful("DOUBLE", type),

        // DuckDB's VARCHAR takes no length, and a declared one is not enforced. Saying so beats
        // rendering VARCHAR(50) and letting somebody believe it is a constraint.
        CanonicalTypeKind.String when type.IsMax || type.Length is null => Faithful("VARCHAR", type),
        CanonicalTypeKind.String => new RenderedColumnType(
            "VARCHAR", Combine(type, "DuckDB VARCHAR has no length bound; it is not enforced at the target.")),

        CanonicalTypeKind.Binary => Faithful("BLOB", type),
        CanonicalTypeKind.Date => Faithful("DATE", type),
        CanonicalTypeKind.Time => Faithful("TIME", type),
        CanonicalTypeKind.Timestamp => (type.Scale ?? 6) > 6
            ? new RenderedColumnType("TIMESTAMP", Combine(type,
                "One digit of sub-second precision lost (SQL Server datetime2 supports 7 digits; DuckDB TIMESTAMP supports 6)."))
            : Faithful("TIMESTAMP", type),
        CanonicalTypeKind.TimestampTz => Faithful("TIMESTAMPTZ", type),
        CanonicalTypeKind.Guid => Faithful("UUID", type),
        CanonicalTypeKind.Json => Faithful("JSON", type),

        // DuckDB has no XML type at all, and its JSON is the nearest thing that stores the text
        // without pretending to validate it.
        CanonicalTypeKind.Xml => new RenderedColumnType(
            "VARCHAR", Combine(type, "DuckDB has no XML type; the document is stored as text and not validated.")),

        CanonicalTypeKind.Unmappable => throw new InvalidOperationException(
            "RenderColumnType must never be called for Unmappable — the caller checks CanonicalType.Kind " +
            "first and reports the plan as Unsupported instead."),

        _ => throw new ArgumentOutOfRangeException(nameof(type), type.Kind, "Unknown canonical type kind."),
    };

    private static RenderedColumnType Faithful(string sql, CanonicalType type) => new(sql, type.SourceNote);

    private static string Combine(CanonicalType type, string renderNote) =>
        type.SourceNote is null ? renderNote : $"{type.SourceNote} {renderNote}";
}
