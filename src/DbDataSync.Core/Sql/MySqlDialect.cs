namespace DbDataSync.Core.Sql;

/// <summary>
/// MySQL and MariaDB, on MySqlConnector.
/// <para>
/// One real, unresolved gap, worth stating up front: <see cref="RenderTieSafeRowLimit"/> cannot be
/// tie-safe here. Every other dialect in scope expresses "cap this ordered read at n rows without
/// splitting ties" as a prefix or suffix wrapped around an already-fixed <c>SELECT ... WHERE ...
/// ORDER BY</c> shape — SQL Server's <c>TOP (n) WITH TIES</c>, the ANSI <c>FETCH FIRST n ROWS WITH
/// TIES</c> Postgres and Oracle both accept. MySQL has no clause like it at all, tie-safe or otherwise,
/// and the only mechanism that WOULD be tie-safe (a window-function rewrite ranking rows by the order
/// column) needs the query restructured around a derived table — which this hook's shape, two string
/// fragments wrapped around a query it does not otherwise touch, has no room for. This renders a plain
/// <c>LIMIT n</c> instead: correct syntax, not tie-safe. A bounded <c>Watermark</c> pass capped
/// mid-tie can skip a sibling row sharing the exact boundary value until a later pass happens to end
/// somewhere the tie does not recur. Real, not theoretical — named in phase 147's own "Open questions"
/// — and not fixed by this phase.
/// </para>
/// <para>
/// <c>RenderRenameColumn</c> is not overridden: the inherited ANSI <c>RENAME COLUMN ... TO ...</c> form
/// is valid MySQL 8.0+/MariaDB 10.5.2+ syntax. An older server needs <c>CHANGE COLUMN old new type</c>
/// instead, which this does not attempt — naming the floor here rather than discovering it as a syntax
/// error against an old server.
/// </para>
/// </summary>
public sealed class MySqlDialect : SqlDialect
{
    public static MySqlDialect Instance { get; } = new();

    private MySqlDialect() { }

    public override string QuoteIdentifier(string identifier) => $"`{identifier.Replace("`", "``")}`";

    public override string ParameterReference(string name) => $"@{name}";

    /// <summary>MySQL's protocol allows up to 65535 placeholders per prepared statement — the same
    /// ceiling Postgres has; SQL Server's 2100 is the outlier, not this.</summary>
    public override int MaxParametersPerStatement => 65535;

    public override string OperationMarkerColumnType => "char(1)";

    /// <summary>MySQL has no <c>datetime2</c>; <c>timestamp(6)</c> is the closest sub-second-precision
    /// equivalent, and matches the type <see cref="DbDataSync.Drivers.MySql.MySqlTriggerAudit"/>'s own
    /// shadow table DDL already uses for <c>DS_ChangedAt</c>.</summary>
    public override string ChangedAtColumnType => "timestamp(6)";

    // UseDatabaseAsync: not overridden. Unlike Postgres and Oracle, a MySqlConnector connection
    // genuinely can change database on a live connection — MySqlConnection.ChangeDatabase is a real
    // override, not an inherited one that throws, confirmed directly against the MySqlConnector 2.4.0
    // assembly rather than assumed. That resolves phase 147's own open "switch, or validate-and-refuse"
    // question: switch, using the connection's real capability. The base SqlDialect implementation
    // (DbConnection.ChangeDatabase) is correct here unmodified.

    /// <inheritdoc/>
    public override (string Prefix, string Suffix) RenderTieSafeRowLimit(string parameterName) =>
        ("", $" LIMIT {ParameterReference(parameterName)}");

    /// <summary>MySQL's identity column, the same role Postgres's <c>BIGSERIAL</c>/
    /// <c>GENERATED ALWAYS AS IDENTITY</c> plays.</summary>
    public override string RenderStagingOrdinalColumn(string column) =>
        $"{QuoteIdentifier(column)} BIGINT AUTO_INCREMENT PRIMARY KEY";

    // RenderInsertInto / WriteWithGeneratedColumnOverrideAsync: not overridden. MySQL accepts an
    // explicit value for an AUTO_INCREMENT column through a plain INSERT — no session flag, no
    // statement-level override clause. SqlDialect's own doc comment says so ("MySQL needs nothing"),
    // and the base implementations of both hooks already do exactly nothing extra.

    /// <summary>MySQL's <c>CAST</c> target list does not include <c>VARCHAR</c> — only <c>CHAR</c> (and
    /// a handful of other fixed types). The ANSI default this overrides would be a syntax error here.</summary>
    public override string CastToText(string expression) => $"CAST({expression} AS CHAR(4000))";

    /// <summary>MySQL has no <c>ALTER TABLE ... ALTER COLUMN ... TYPE ...</c>; a type change is a full
    /// column redefinition via <c>MODIFY COLUMN</c>. Explicitly nullable, like every other dialect's
    /// override of this hook: a table with rows cannot gain a NOT NULL column for free, and MODIFY
    /// redefines the whole column, so leaving nullability unstated here would let a bare column
    /// definition's own default decide it instead of this method's own explicit answer.</summary>
    public override string? RenderAlterColumnType(string qualifiedTable, string column, string type) =>
        $"ALTER TABLE {qualifiedTable} MODIFY COLUMN {QuoteIdentifier(column)} {type} NULL";

    /// <summary>MySQL user variables share the exact <c>@name</c> syntax <see cref="ParameterReference"/>
    /// already uses for bind parameters, and are untyped — a plain <c>SET @p = value;</c> per parameter,
    /// no declared type needed.</summary>
    public override string? RenderDeclarations(IReadOnlyList<PreviewParameter> parameters) =>
        parameters.Count == 0 ? null : string.Join(
            "\n", parameters.Select(p => $"SET {ParameterReference(p.Name)} = {p.Literal};"));

    public override BucketableKind ClassifyForBucketing(string baseTypeName) => baseTypeName switch
    {
        "tinyint" or "smallint" or "mediumint" or "int" or "integer" or "bigint" => BucketableKind.Integral,
        "decimal" or "numeric" or "float" or "double" => BucketableKind.Numeric,
        "date" or "datetime" or "timestamp" => BucketableKind.DateTime,
        _ => base.ClassifyForBucketing(baseTypeName),
    };

    /// <summary>See the type-mapping table in phase 25 §2. <c>ENUM</c>, <c>SET</c>, <c>BIT</c> and the
    /// spatial types have no cross-engine equivalent and fall through to
    /// <see cref="CanonicalTypeKind.Unmappable"/> — no coercion layer guesses a rendering for any of
    /// them. <c>tinyint(1)</c> is MySQL's own boolean convention (what every ORM and the server's own
    /// tools already treat it as); a bare <c>tinyint</c> with no display width stays
    /// <see cref="CanonicalTypeKind.Int8"/>.</summary>
    public override CanonicalType ToCanonicalType(string nativeType)
    {
        var (baseName, args) = CanonicalTypeSpec.Parse(nativeType);
        return baseName switch
        {
            "tinyint" when CanonicalTypeSpec.IntAt(args, 0, null) == 1 => Simple(CanonicalTypeKind.Boolean),
            "tinyint" => Simple(CanonicalTypeKind.Int8),
            "smallint" => Simple(CanonicalTypeKind.Int16),
            "mediumint" or "int" or "integer" => Simple(CanonicalTypeKind.Int32),
            "bigint" => Simple(CanonicalTypeKind.Int64),

            "decimal" or "numeric" => new CanonicalType(
                CanonicalTypeKind.Decimal, null, CanonicalTypeSpec.IntAt(args, 0, 10), CanonicalTypeSpec.IntAt(args, 1, 0), false, false),

            "float" => Simple(CanonicalTypeKind.Float),
            "double" or "double precision" or "real" => Simple(CanonicalTypeKind.Double),

            "varchar" =>
                new CanonicalType(CanonicalTypeKind.String, CanonicalTypeSpec.IntAt(args, 0, null), null, null, true, false),
            "char" =>
                new CanonicalType(CanonicalTypeKind.String, CanonicalTypeSpec.IntAt(args, 0, 1), null, null, true, false),
            "tinytext" or "text" or "mediumtext" or "longtext" =>
                new CanonicalType(CanonicalTypeKind.String, null, null, null, true, true),

            "binary" =>
                new CanonicalType(CanonicalTypeKind.Binary, CanonicalTypeSpec.IntAt(args, 0, 1), null, null, false, false, IsFixed: true),
            "varbinary" =>
                new CanonicalType(CanonicalTypeKind.Binary, CanonicalTypeSpec.IntAt(args, 0, null), null, null, false, false),
            "tinyblob" or "blob" or "mediumblob" or "longblob" =>
                new CanonicalType(CanonicalTypeKind.Binary, null, null, null, false, true),

            "date" => Simple(CanonicalTypeKind.Date),
            "time" => new CanonicalType(CanonicalTypeKind.Time, null, null, CanonicalTypeSpec.IntAt(args, 0, 0), false, false),
            "datetime" =>
                new CanonicalType(CanonicalTypeKind.Timestamp, null, null, CanonicalTypeSpec.IntAt(args, 0, 0), false, false),

            "timestamp" => new CanonicalType(CanonicalTypeKind.TimestampTz, null, null, CanonicalTypeSpec.IntAt(args, 0, 0), false, false,
                SourceNote: "MySQL TIMESTAMP is stored in UTC and converted to/from the session time zone at " +
                    "read and write; it carries no explicit offset of its own the way a true zone-aware type does."),

            "json" => Simple(CanonicalTypeKind.Json),

            // enum/set/bit/geometry and the other spatial types have no faithful cross-engine target —
            // none of these get a guessed rendering.
            _ => Simple(CanonicalTypeKind.Unmappable),
        };

        static CanonicalType Simple(CanonicalTypeKind kind) => new(kind, null, null, null, false, false);
    }

    public override RenderedColumnType RenderColumnType(CanonicalType type) => type.Kind switch
    {
        CanonicalTypeKind.Boolean => Faithful("tinyint(1)", type),
        CanonicalTypeKind.Int8 => Faithful("tinyint", type),
        CanonicalTypeKind.Int16 => Faithful("smallint", type),
        CanonicalTypeKind.Int32 => Faithful("int", type),
        CanonicalTypeKind.Int64 => Faithful("bigint", type),
        CanonicalTypeKind.Decimal => Faithful($"decimal({type.Precision ?? 10},{type.Scale ?? 0})", type),
        CanonicalTypeKind.Float => Faithful("float", type),
        CanonicalTypeKind.Double => Faithful("double", type),

        CanonicalTypeKind.String when type.IsMax =>
            new RenderedColumnType("longtext", Combine(type, "No length bound at the target.")),
        CanonicalTypeKind.String => (type.Length ?? 255) > 65535
            ? new RenderedColumnType("longtext", Combine(type, "Exceeds VARCHAR's 65535-byte row-length ceiling; rendered as LONGTEXT instead."))
            : Faithful($"varchar({type.Length ?? 255})", type),

        CanonicalTypeKind.Binary => type.IsMax || type.Length is null
            ? new RenderedColumnType("longblob", type.SourceNote)
            : type.IsFixed
                ? Faithful($"binary({type.Length})", type)
                : Faithful($"varbinary({type.Length})", type),

        CanonicalTypeKind.Date => Faithful("date", type),
        CanonicalTypeKind.Time => Faithful($"time({Math.Min(type.Scale ?? 6, 6)})", type),

        CanonicalTypeKind.Timestamp => (type.Scale ?? 6) > 6
            ? new RenderedColumnType("datetime(6)", Combine(type,
                "One digit of sub-second precision lost (SQL Server datetime2 supports 7 digits; MySQL DATETIME supports 6)."))
            : Faithful($"datetime({type.Scale ?? 6})", type),

        CanonicalTypeKind.TimestampTz => new RenderedColumnType($"timestamp({Math.Min(type.Scale ?? 6, 6)})",
            Combine(type, "MySQL TIMESTAMP carries no explicit offset; it is stored in UTC and converted " +
                "through the session time zone, which is not the same guarantee as a source zone-aware value.")),

        CanonicalTypeKind.Guid => Faithful("char(36)", type with
        {
            SourceNote = Combine(type, "MySQL has no native GUID/UUID type; stored as its canonical 36-character text form."),
        }),
        CanonicalTypeKind.Json => Faithful("json", type),
        CanonicalTypeKind.Xml => Faithful("longtext", type with
        {
            SourceNote = Combine(type, "MySQL has no native XML type; stored as text."),
        }),

        CanonicalTypeKind.Unmappable => throw new InvalidOperationException(
            "RenderColumnType must never be called for Unmappable — the caller checks CanonicalType.Kind " +
            "first and reports the plan as Unsupported instead."),

        _ => throw new ArgumentOutOfRangeException(nameof(type), type.Kind, "Unknown canonical type kind."),
    };

    private static RenderedColumnType Faithful(string sql, CanonicalType type) => new(sql, type.SourceNote);

    private static string Combine(CanonicalType type, string renderNote) =>
        type.SourceNote is null ? renderNote : $"{type.SourceNote} {renderNote}";
}
