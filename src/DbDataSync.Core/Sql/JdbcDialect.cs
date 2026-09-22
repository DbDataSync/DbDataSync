namespace DbDataSync.Core.Sql;

/// <summary>
/// Phase 165V's reader spike, generic JDBC over IKVM — scoped to exactly what that spike's one Postgres
/// test table needs, not a general cross-engine JDBC type matrix. Whether a JDBC engine ever gets a
/// richer, engine-agnostic dialect of its own is <c>architecture/planning/todo/jdbc-driver-support.md</c>'s
/// own still-open question (its "Open questions" §1) — this class deliberately does not try to answer it.
/// <para>
/// <see cref="ParameterReference"/> renders a named marker (<c>@name</c>), the same look every other
/// dialect here uses — not JDBC's own bare <c>?</c>. <c>DbDataSync.Drivers.Jdbc</c>'s <c>JdbcCommand</c>
/// is what translates named markers to ordinal <c>?</c> placeholders at execute time (the same shape
/// <c>OdbcCommand</c> uses for the identical ordinal-only-placeholder problem) — see phase 165V.
/// </para>
/// <para>
/// <see cref="ToCanonicalType"/>/<see cref="RenderColumnType"/> cover exactly the native type names
/// pgJDBC's own <c>DatabaseMetaData</c> reports for Postgres (<c>int4</c>, <c>int8</c>, <c>varchar</c>,
/// <c>numeric</c>, <c>timestamp</c>, <c>bool</c> — the same pg_catalog spellings
/// <see cref="PostgresDialect"/> already maps, since pgJDBC's metadata queries pg_catalog directly), not
/// a generic mapping from every JDBC vendor's own type names.
/// </para>
/// </summary>
public sealed class JdbcDialect : SqlDialect
{
    public static JdbcDialect Instance { get; } = new();

    private JdbcDialect() { }

    public override string QuoteIdentifier(string identifier) => $"\"{identifier.Replace("\"", "\"\"")}\"";

    public override string ParameterReference(string name) => $"@{name}";

    /// <summary>
    /// Bare, no <c>@</c> — the divergence from <see cref="ParameterReference"/> <see
    /// cref="SqlDialect.ParameterName"/>'s own doc comment names ("some providers want the bare name
    /// without its sigil"). Every other dialect here leaves the two identical because the real ADO.NET
    /// provider underneath (SqlClient, Npgsql, MySqlConnector) tolerates a <c>DbParameter.ParameterName</c>
    /// carrying the <c>@</c>/<c>:</c> sigil too — but <c>JdbcCommand</c>'s own name→position translation
    /// (see its class doc) is the thing doing the matching here, not a real provider, and it matches
    /// <see cref="ParameterReference"/>'s captured bare name against <c>DbParameter.ParameterName</c>
    /// directly. Found by running the real parity tests, not by inspection — the mismatch (parameters
    /// stored as <c>"@name"</c>, looked up as <c>"name"</c>) threw "no matching entry in Parameters" the
    /// first time a bound watermark reached <c>JdbcCommand</c>.
    /// </summary>
    public override string ParameterName(string name) => name;

    public override CanonicalType ToCanonicalType(string nativeType)
    {
        var (baseName, args) = CanonicalTypeSpec.Parse(nativeType);
        return baseName switch
        {
            "bool" or "boolean" => Simple(CanonicalTypeKind.Boolean),
            "int2" or "smallint" => Simple(CanonicalTypeKind.Int16),
            "int4" or "integer" => Simple(CanonicalTypeKind.Int32),
            "int8" or "bigint" => Simple(CanonicalTypeKind.Int64),

            "numeric" or "decimal" => new CanonicalType(
                CanonicalTypeKind.Decimal, null, CanonicalTypeSpec.IntAt(args, 0, 18), CanonicalTypeSpec.IntAt(args, 1, 0), false, false),

            "float4" or "real" => Simple(CanonicalTypeKind.Float),
            "float8" or "double precision" => Simple(CanonicalTypeKind.Double),

            "varchar" or "character varying" =>
                new CanonicalType(CanonicalTypeKind.String, CanonicalTypeSpec.IntAt(args, 0, null), null, null, true, false),
            "text" => new CanonicalType(CanonicalTypeKind.String, null, null, null, true, true),

            "timestamp" =>
                new CanonicalType(CanonicalTypeKind.Timestamp, null, null, CanonicalTypeSpec.IntAt(args, 0, 6), false, false),
            "timestamptz" =>
                new CanonicalType(CanonicalTypeKind.TimestampTz, null, null, CanonicalTypeSpec.IntAt(args, 0, 6), false, false),
            "date" => Simple(CanonicalTypeKind.Date),

            // Everything else is genuinely out of scope for this spike's dialect — see the class doc.
            _ => Simple(CanonicalTypeKind.Unmappable),
        };

        static CanonicalType Simple(CanonicalTypeKind kind) => new(kind, null, null, null, false, false);
    }

    public override RenderedColumnType RenderColumnType(CanonicalType type) => type.Kind switch
    {
        CanonicalTypeKind.Boolean => new("boolean", type.SourceNote),
        CanonicalTypeKind.Int16 => new("smallint", type.SourceNote),
        CanonicalTypeKind.Int32 => new("integer", type.SourceNote),
        CanonicalTypeKind.Int64 => new("bigint", type.SourceNote),
        CanonicalTypeKind.Decimal => new($"numeric({type.Precision ?? 18},{type.Scale ?? 0})", type.SourceNote),
        CanonicalTypeKind.Float => new("real", type.SourceNote),
        CanonicalTypeKind.Double => new("double precision", type.SourceNote),
        CanonicalTypeKind.String when type.IsMax => new("text", type.SourceNote),
        CanonicalTypeKind.String => new($"varchar({type.Length ?? 255})", type.SourceNote),
        CanonicalTypeKind.Date => new("date", type.SourceNote),
        CanonicalTypeKind.Timestamp => new($"timestamp({type.Scale ?? 6})", type.SourceNote),
        CanonicalTypeKind.TimestampTz => new("timestamptz", type.SourceNote),

        CanonicalTypeKind.Unmappable => throw new InvalidOperationException(
            "RenderColumnType must never be called for Unmappable — the caller checks CanonicalType.Kind " +
            "first and reports the plan as Unsupported instead."),

        _ => throw new NotSupportedException(
            $"JdbcDialect.RenderColumnType: '{type.Kind}' is out of scope for phase 165V's spike dialect."),
    };
}
