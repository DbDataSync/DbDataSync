namespace DbDataSync.Core.Sql;

/// <summary>
/// Oracle, on Oracle.ManagedDataAccess.Core (ODP.NET Core).
/// <para>
/// Two real findings from testing this against a live Oracle 23ai instance, not assumed from the ANSI
/// standard or from Postgres's own precedent — both worth stating up front, since both contradict what
/// a first draft of this dialect would reasonably have assumed:
/// </para>
/// <para>
/// **`OVERRIDING SYSTEM VALUE` does not parse on Oracle at all**, in any form (`INSERT ... VALUES`,
/// `INSERT ... SELECT`, or as a `MERGE`'s `INSERT` clause) — confirmed via `sqlplus`, ruling out an
/// ODP.NET client-side quirk. This is the ANSI SQL:2003 clause Postgres uses for exactly this purpose
/// (see <c>PostgresDialect.RenderInsertInto</c>), and Oracle's own SQL Language Reference describes it
/// for identity columns — but it is not what this server actually accepts. See
/// <see cref="WriteWithGeneratedColumnOverrideAsync"/>.
/// </para>
/// <para>
/// **`UseDatabaseAsync` cannot validate anything here.** Oracle connections are bound to one service
/// for their lifetime, the same as Postgres — but unlike Postgres, where <c>connection.Database</c> is
/// the connected database's real name, <c>OracleConnection.Database</c> is confirmed (by reflection and
/// by a live connection) to be an empty string regardless of what the connection is actually pointed
/// at. The value that *is* populated, <c>OracleConnection.ServiceName</c>, is Oracle-specific and
/// reaching it would mean <c>DbDataSync.Core</c> referencing <c>Oracle.ManagedDataAccess.Core</c> — a
/// package dependency no dialect in this project has, and a real layering line worth keeping. So this
/// override does not validate at all, rather than fabricate a check against a property that carries
/// nothing.
/// </para>
/// </summary>
public sealed class OracleDialect : SqlDialect
{
    public static OracleDialect Instance { get; } = new();

    private OracleDialect() { }

    public override string QuoteIdentifier(string identifier) => $"\"{identifier.Replace("\"", "\"\"")}\"";

    public override string ParameterReference(string name) => $":{name}";

    /// <summary>
    /// A real, tested divergence from every other driver here: <c>OracleParameter.ParameterName</c>
    /// rejects a colon baked into it (<c>ORA-01745: invalid host/bind variable name</c>) — confirmed by
    /// running the generic pipeline against a live server, not assumed. SQL Server and Postgres/MySQL's
    /// providers tolerate the sigil either way, which is exactly why <see cref="SqlDialect"/>'s own doc
    /// comment on the base <c>ParameterName</c> already anticipated a provider that would not: "some
    /// providers want the bare name without its sigil, which is why the two are separate." Oracle is
    /// that provider.
    /// </summary>
    public override string ParameterName(string name) => name;

    // MaxParametersPerStatement: not overridden. Oracle's real bind-variable ceiling for one statement
    // is high, but this was not measured against a live server the way phase 147's MySQL/Postgres
    // figures were, and the base class's own doc comment is explicit about the consequence of
    // guessing wrong here: "a dialect that forgets to state its own limit should batch too
    // conservatively rather than emit a statement the server rejects." Inherits SqlDialect's
    // conservative default (2100) until someone measures the real one.

    public override string OperationMarkerColumnType => "char(1)";

    public override string ChangedAtColumnType => "timestamp(6)";

    /// <summary>See this class's own doc comment — there is genuinely nothing to validate against
    /// using only <see cref="System.Data.Common.DbConnection"/>'s provider-agnostic surface.</summary>
    public override Task UseDatabaseAsync(
        System.Data.Common.DbConnection connection, string database, CancellationToken cancellationToken) =>
        Task.CompletedTask;

    /// <summary>
    /// <c>GENERATED ALWAYS AS IDENTITY</c> — matches <see cref="DbDataSync.Drivers.Oracle.OracleTriggerAudit"/>'s
    /// own shadow table sequence column, and correct here too: nothing ever supplies an explicit value
    /// for the staging table's own ordinal, so the fact that Oracle has no working override mechanism
    /// for this generation mode (see <see cref="WriteWithGeneratedColumnOverrideAsync"/>) never matters
    /// for this column.
    /// </summary>
    public override string RenderStagingOrdinalColumn(string column) =>
        $"{QuoteIdentifier(column)} NUMBER GENERATED ALWAYS AS IDENTITY PRIMARY KEY";

    /// <summary>
    /// Oracle has no <c>VALUES (…), (…)</c> multi-row form, and <c>INSERT ALL</c> — the form this once
    /// rendered — turned out to be the wrong choice for exactly the table this hook exists to serve:
    /// confirmed against a live server that <c>INSERT ALL</c>'s multiple <c>INTO</c> branches all see
    /// the *same* generated identity/sequence value, not one each, because Oracle evaluates a sequence
    /// (identity columns included — they're sequence-backed under the hood) at most once per statement,
    /// however many times it's referenced within it. A staging table's own ordinal column
    /// (<see cref="RenderStagingOrdinalColumn"/>) needs a distinct value per row, so a multi-row
    /// <c>INSERT ALL</c> against it reliably raised a unique-constraint violation on the second row.
    /// <para>
    /// <c>INSERT INTO t (cols) SELECT * FROM (SELECT … FROM dual UNION ALL SELECT … FROM dual …)</c> —
    /// an ordinary <c>INSERT ... SELECT</c> whose source happens to be a literal row per branch — does
    /// not have this problem: it inserts one row at a time from the driving query's own result set, the
    /// same shape as any other multi-row <c>INSERT ... SELECT</c>, so identity generation fires once
    /// per row exactly as it does for a real query. Confirmed against a live server to generate distinct
    /// sequential values for every row in one statement, which <c>INSERT ALL</c> never did.
    /// </para>
    /// </summary>
    public override string RenderMultiRowInsert(string qualifiedTable, string columnList, IReadOnlyList<string> rowValueTuples)
    {
        // Each tuple arrives as "(v0, v1, …)"; the outer parens are stripped rather than trimmed
        // character-by-character, since a value itself could legitimately end in ')' (a function call)
        // and Trim('(', ')') would eat that too.
        var rows = rowValueTuples.Select(tuple => $"SELECT {tuple[1..^1]} FROM dual");
        return $"""
            INSERT INTO {qualifiedTable} ({columnList})
            SELECT * FROM (
                {string.Join("\n    UNION ALL ", rows)}
            )
            """;
    }

    /// <summary>
    /// A real, tested constraint, not a design preference: confirmed against a live Oracle 23ai
    /// instance (via <c>sqlplus</c>, so not an ODP.NET client quirk) that <c>OVERRIDING SYSTEM VALUE</c>
    /// does not parse in any statement shape, and that a <c>GENERATED ALWAYS AS IDENTITY</c> column
    /// rejects an explicit value outright (<c>ORA-32795</c>) with no session-level escape hatch the way
    /// SQL Server's <c>SET IDENTITY_INSERT</c> is. The one DDL-based workaround —
    /// <c>ALTER TABLE ... MODIFY id GENERATED BY DEFAULT ON NULL AS IDENTITY</c> around the write, then
    /// back — is not offered here: Oracle DDL commits implicitly, so running it around a write would
    /// silently end whatever transaction the caller is in, which is worse than the write failing.
    /// <para>
    /// The one mechanism that *is* confirmed to work, by contrast, needs no override machinery at all:
    /// a target column declared <c>GENERATED BY DEFAULT ON NULL AS IDENTITY</c> accepts an explicit
    /// value through a plain <c>INSERT</c>, verified directly. An Oracle target table that needs
    /// explicit-value round-tripping (mirroring a source's own identity column) should be created that
    /// way — a provisioning/documentation recommendation, not something this hook can retrofit onto a
    /// table it did not create.
    /// </para>
    /// </summary>
    public override Task<T> WriteWithGeneratedColumnOverrideAsync<T>(
        System.Data.Common.DbConnection connection,
        System.Data.Common.DbTransaction? transaction,
        string qualifiedTable,
        bool overrideRequired,
        Func<Task<T>> write,
        CancellationToken cancellationToken) => write();

    /// <summary>Oracle's <c>CAST</c> accepts neither bare identifiers as its own target-type keyword
    /// set includes no ambiguity here — <c>VARCHAR2</c> is the faithful choice, unlike MySQL's
    /// <c>CAST</c> restriction to <c>CHAR</c>.</summary>
    public override string CastToText(string expression) => $"CAST({expression} AS VARCHAR2(4000))";

    /// <summary><c>NUMBER(1)</c> with <c>1</c>/<c>0</c> — Oracle has no native boolean column type
    /// before 23c, and this driver's floor (see <c>OracleTriggerAudit</c>'s own identity-column
    /// decision) does not assume 23c+. A deliberate convention, not a default nobody chose.</summary>
    public override string TrueLiteral => "1";

    public override string FalseLiteral => "0";

    public override BucketableKind ClassifyForBucketing(string baseTypeName) => baseTypeName switch
    {
        "number" => BucketableKind.Numeric,
        "binary_float" or "binary_double" => BucketableKind.Numeric,
        "date" or "timestamp" => BucketableKind.DateTime,
        _ => base.ClassifyForBucketing(baseTypeName),
    };

    /// <summary>
    /// See the type-mapping table in phase 25 §2. Oracle's <c>DATE</c> carries a full date and
    /// time-of-day with no sub-second component — not the same domain as ANSI <c>DATE</c> — so it maps
    /// to <see cref="CanonicalTypeKind.Timestamp"/> at scale 0, not <see cref="CanonicalTypeKind.Date"/>;
    /// treating it as date-only would silently drop the time-of-day every real Oracle <c>DATE</c> value
    /// carries. <c>ROWID</c>/<c>UROWID</c> have no cross-engine equivalent and fall through to
    /// <see cref="CanonicalTypeKind.Unmappable"/>.
    /// <para>
    /// A bare <c>NUMBER</c> (no declared precision/scale — confirmed against a live server that this is
    /// exactly what an identity column's own catalog row looks like too) is genuinely ambiguous: it can
    /// hold anything from a single digit to a 38-digit value with an arbitrary number of decimal places.
    /// Approximated as <c>Decimal(38,10)</c> with a <see cref="CanonicalType.SourceNote"/> saying so,
    /// rather than <see cref="CanonicalTypeKind.Unmappable"/> — a bare <c>NUMBER</c> is too common a
    /// column shape in real Oracle schemas to refuse outright, but an operator who knows the real
    /// domain should override it explicitly rather than trust the approximation.
    /// </para>
    /// </summary>
    public override CanonicalType ToCanonicalType(string nativeType)
    {
        var trimmed = nativeType.Trim();
        var upper = trimmed.ToUpperInvariant();

        // TIMESTAMP's own DATA_TYPE spelling already embeds its precision ("TIMESTAMP(6)"), unlike
        // every other Oracle type this dialect sees from OracleCatalog — confirmed against a live
        // server rather than assumed. The WITH [LOCAL] TIME ZONE suffix sits after that closing paren,
        // which CanonicalTypeSpec.Parse's own truncation-at-first-')' would silently drop, so both
        // variants are matched here, on the raw string, before falling through to the shared parser.
        if (upper.Contains("WITH TIME ZONE") || upper.Contains("WITH LOCAL TIME ZONE"))
        {
            var (_, tzArgs) = CanonicalTypeSpec.Parse(trimmed);
            return new CanonicalType(
                CanonicalTypeKind.TimestampTz, null, null, CanonicalTypeSpec.IntAt(tzArgs, 0, 6), false, false);
        }

        var (baseName, args) = CanonicalTypeSpec.Parse(trimmed);
        return baseName switch
        {
            "number" when args.Count == 0 => new CanonicalType(
                CanonicalTypeKind.Decimal, null, 38, 10, false, false,
                SourceNote: "Oracle NUMBER with no declared precision/scale; approximated as DECIMAL(38,10) " +
                    "— an operator-provided type override is the more precise route for a genuinely " +
                    "unconstrained column."),
            "number" => ClassifyNumber(CanonicalTypeSpec.IntAt(args, 0, 38)!.Value, CanonicalTypeSpec.IntAt(args, 1, 0)!.Value),

            "binary_float" => Simple(CanonicalTypeKind.Float),
            "binary_double" => Simple(CanonicalTypeKind.Double),

            "varchar2" =>
                new CanonicalType(CanonicalTypeKind.String, CanonicalTypeSpec.IntAt(args, 0, null), null, null, false, false),
            "nvarchar2" =>
                new CanonicalType(CanonicalTypeKind.String, CanonicalTypeSpec.IntAt(args, 0, null), null, null, true, false),
            "char" =>
                new CanonicalType(CanonicalTypeKind.String, CanonicalTypeSpec.IntAt(args, 0, 1), null, null, false, false),
            "nchar" =>
                new CanonicalType(CanonicalTypeKind.String, CanonicalTypeSpec.IntAt(args, 0, 1), null, null, true, false),
            "clob" => new CanonicalType(CanonicalTypeKind.String, null, null, null, false, true),
            "nclob" => new CanonicalType(CanonicalTypeKind.String, null, null, null, true, true),

            "raw" =>
                new CanonicalType(CanonicalTypeKind.Binary, CanonicalTypeSpec.IntAt(args, 0, null), null, null, false, false),
            "long raw" or "blob" => new CanonicalType(CanonicalTypeKind.Binary, null, null, null, false, true),

            // No sub-second component — see this method's own doc comment.
            "date" => new CanonicalType(CanonicalTypeKind.Timestamp, null, null, 0, false, false),
            "timestamp" =>
                new CanonicalType(CanonicalTypeKind.Timestamp, null, null, CanonicalTypeSpec.IntAt(args, 0, 6), false, false),

            "xmltype" => Simple(CanonicalTypeKind.Xml),

            // rowid/urowid/bfile and Oracle's own spatial (SDO_GEOMETRY) and JSON-as-a-distinct-type
            // (23c+, out of scope per this driver's 12c+ floor) have no faithful cross-engine target —
            // none of these get a guessed rendering.
            _ => Simple(CanonicalTypeKind.Unmappable),
        };

        static CanonicalType Simple(CanonicalTypeKind kind) => new(kind, null, null, null, false, false);

        static CanonicalType ClassifyNumber(int precision, int scale) => scale switch
        {
            0 when precision <= 4 => Simple(CanonicalTypeKind.Int16),
            0 when precision <= 9 => Simple(CanonicalTypeKind.Int32),
            0 when precision <= 18 => Simple(CanonicalTypeKind.Int64),
            _ => new CanonicalType(CanonicalTypeKind.Decimal, null, precision, scale, false, false),
        };
    }

    public override RenderedColumnType RenderColumnType(CanonicalType type) => type.Kind switch
    {
        CanonicalTypeKind.Boolean => Faithful("number(1)", type),
        CanonicalTypeKind.Int8 => Faithful("number(3)", type),
        CanonicalTypeKind.Int16 => Faithful("number(5)", type),
        CanonicalTypeKind.Int32 => Faithful("number(10)", type),
        CanonicalTypeKind.Int64 => Faithful("number(19)", type),
        CanonicalTypeKind.Decimal => Faithful($"number({type.Precision ?? 38},{type.Scale ?? 10})", type),
        CanonicalTypeKind.Float => Faithful("binary_float", type),
        CanonicalTypeKind.Double => Faithful("binary_double", type),

        CanonicalTypeKind.String when type.IsMax =>
            new RenderedColumnType(type.IsUnicode ? "nclob" : "clob", Combine(type, "No length bound at the target.")),
        CanonicalTypeKind.String => Faithful(
            type.IsUnicode ? $"nvarchar2({type.Length ?? 255})" : $"varchar2({type.Length ?? 255})", type),

        CanonicalTypeKind.Binary => type.IsMax || type.Length is null
            ? new RenderedColumnType("blob", type.SourceNote)
            : (type.Length ?? 0) > 2000
                ? new RenderedColumnType("blob", Combine(type, "Exceeds RAW's 2000-byte column limit; rendered as BLOB instead."))
                : Faithful($"raw({type.Length})", type),

        // See this class's ToCanonicalType doc comment: Oracle DATE is a low-precision timestamp, not
        // a date-only type, so a canonical Date still renders faithfully as DATE — nothing is lost
        // going *into* Oracle, only coming from it.
        CanonicalTypeKind.Date => Faithful("date", type),

        CanonicalTypeKind.Time => new RenderedColumnType(
            $"timestamp({Math.Min(type.Scale ?? 6, 9)})",
            Combine(type, "Oracle has no native TIME-only type; stored as TIMESTAMP, whose date portion is meaningless.")),

        CanonicalTypeKind.Timestamp => Faithful($"timestamp({Math.Min(type.Scale ?? 6, 9)})", type),
        CanonicalTypeKind.TimestampTz => Faithful($"timestamp({Math.Min(type.Scale ?? 6, 9)}) with time zone", type),

        CanonicalTypeKind.Guid => Faithful("varchar2(36)", type with
        {
            SourceNote = Combine(type, "Oracle has no native GUID/UUID type; stored as its canonical 36-character text form."),
        }),
        // JSON as a distinct column type needs Oracle 21c+; this driver's floor is 12c+ (see
        // OracleTriggerAudit), so CLOB is the broadly-compatible choice rather than a keyword that
        // would fail to parse on an in-scope but pre-21c target.
        CanonicalTypeKind.Json => new RenderedColumnType("clob", Combine(type,
            "Oracle's native JSON column type needs 21c+; stored as CLOB, unvalidated, for broader version compatibility.")),
        CanonicalTypeKind.Xml => Faithful("xmltype", type),

        CanonicalTypeKind.Unmappable => throw new InvalidOperationException(
            "RenderColumnType must never be called for Unmappable — the caller checks CanonicalType.Kind " +
            "first and reports the plan as Unsupported instead."),

        _ => throw new ArgumentOutOfRangeException(nameof(type), type.Kind, "Unknown canonical type kind."),
    };

    private static RenderedColumnType Faithful(string sql, CanonicalType type) => new(sql, type.SourceNote);

    private static string Combine(CanonicalType type, string renderNote) =>
        type.SourceNote is null ? renderNote : $"{type.SourceNote} {renderNote}";
}
