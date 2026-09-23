using System.Data.Common;

namespace DbDataSync.Core.Sql;

/// <summary>SQL Server's answers to the mechanical variations in <see cref="SqlDialect"/>: bracket
/// quoting and <c>@</c>-prefixed parameters, both in statement text and when binding.</summary>
public sealed class MsSqlDialect : SqlDialect
{
    public static MsSqlDialect Instance { get; } = new();

    private MsSqlDialect() { }

    public override string QuoteIdentifier(string identifier) => $"[{identifier.Replace("]", "]]")}]";

    public override string ParameterReference(string name) => $"@{name}";

    /// <summary>SQL Server caps a request at 2100 parameters.</summary>
    public override int MaxParametersPerStatement => 2100;

    /// <summary>SQL Server puts its row limit at the front, and has no <c>FETCH FIRST … WITH TIES</c>.
    /// The parentheses around the parameter are required for a parameterised <c>TOP</c>.</summary>
    public override (string Prefix, string Suffix) RenderTieSafeRowLimit(string parameterName) =>
        ($"TOP ({ParameterReference(parameterName)}) WITH TIES ", "");

    /// <summary>SQL Server has no <c>GENERATED ALWAYS AS IDENTITY</c>; the key is clustered because
    /// staging is only ever appended to in ordinal order, so the insert stays at the end of it.</summary>
    public override string RenderStagingOrdinalColumn(string column) =>
        $"{QuoteIdentifier(column)} BIGINT IDENTITY(1,1) NOT NULL PRIMARY KEY CLUSTERED";

    /// <summary>The spellings the shared list does not carry. Everything else falls through to the
    /// base classification, so this is SQL Server's additions rather than a restatement.</summary>
    public override BucketableKind ClassifyForBucketing(string baseTypeName) => baseTypeName switch
    {
        "money" or "smallmoney" => BucketableKind.Numeric,
        "smalldatetime" or "datetime2" => BucketableKind.DateTime,
        "datetimeoffset" => BucketableKind.DateTimeOffset,
        _ => base.ClassifyForBucketing(baseTypeName),
    };

    /// <summary>Explicit values for an IDENTITY column need the session flag, which SQL Server permits
    /// on one table at a time — so it is always turned back off, including when the write fails.</summary>
    public override Task<T> WriteWithGeneratedColumnOverrideAsync<T>(
        DbConnection connection,
        DbTransaction? transaction,
        string qualifiedTable,
        bool overrideRequired,
        Func<Task<T>> write,
        CancellationToken cancellationToken) =>
        MsSqlIdentityInsert.RunAsync(connection, transaction, qualifiedTable, overrideRequired, write, cancellationToken);

    /// <summary>See the type-mapping table in phase 25 §2 — the pairs listed there are what the tests
    /// pin. <c>sql_variant</c>, <c>hierarchyid</c> and geospatial types fall through to
    /// <see cref="CanonicalTypeKind.Unmappable"/>: nothing here guesses a rendering for them.</summary>
    /// <summary>SQL Server's ADD takes no COLUMN keyword.</summary>
    public override string RenderAddColumn(string qualifiedTable, string column, string type) =>
        $"ALTER TABLE {qualifiedTable} ADD {QuoteIdentifier(column)} {type} NULL";

    /// <summary>SQL Server spells the type change ALTER COLUMN &lt;name&gt; &lt;type&gt;, with no TYPE
    /// keyword.</summary>
    public override string? RenderAlterColumnType(string qualifiedTable, string column, string type) =>
        $"ALTER TABLE {qualifiedTable} ALTER COLUMN {QuoteIdentifier(column)} {type} NULL";

    /// <summary>
    /// SQL Server has no <c>ALTER TABLE … RENAME COLUMN</c>; the rename is a stored procedure whose
    /// first argument is the column's *qualified* name as a string literal and whose second is the new
    /// name **unqualified** — passing a qualified new name is the classic way to end up with a column
    /// literally called <c>dbo.T.NewName</c>.
    /// </summary>
    public override string RenderRenameColumn(string qualifiedTable, string from, string to) =>
        $"EXEC sp_rename N'{Literal($"{qualifiedTable}.{QuoteIdentifier(from)}")}', N'{Literal(to)}', 'COLUMN'";

    private static string Literal(string value) => value.Replace("'", "''");

    /// <summary>SQL Server wraps identifiers in brackets, not ANSI double quotes.</summary>
    protected override char IdentifierQuoteOpen => '[';

    protected override char IdentifierQuoteClose => ']';

    /// <summary>SQL Server's canonical Boolean is <c>bit</c>, which takes 1 and 0 and refuses TRUE.</summary>
    public override string TrueLiteral => "1";

    public override string FalseLiteral => "0";

    /// <summary>SQL Server concatenates with <c>+</c>; <c>||</c> is a syntax error.</summary>
    public override string Concat(IEnumerable<string> expressions) => string.Join(" + ", expressions);

    /// <summary>SQL Server has no LIMIT.</summary>
    public override string RenderSampleSelect(string qualifiedTable, int rows) =>
        $"SELECT TOP ({rows}) * FROM {qualifiedTable}";

    /// <summary>Plain <c>DECLARE @name type = literal;</c>, one per line — valid on its own ahead of
    /// any batch, which is what makes pasting this block followed by the statement it belongs to just
    /// work in SSMS or any other tool that runs a script rather than one bound statement at a time.</summary>
    public override string? RenderDeclarations(IReadOnlyList<PreviewParameter> parameters) =>
        parameters.Count == 0 ? null : string.Join(
            "\n", parameters.Select(p => $"DECLARE {ParameterReference(p.Name)} {p.SqlType} = {p.Literal};"));

    public override CanonicalType ToCanonicalType(string nativeType)
    {
        var (baseName, args) = CanonicalTypeSpec.Parse(nativeType);
        return baseName switch
        {
            "bit" => Simple(CanonicalTypeKind.Boolean),
            "tinyint" => Simple(CanonicalTypeKind.Int8),
            "smallint" => Simple(CanonicalTypeKind.Int16),
            "int" => Simple(CanonicalTypeKind.Int32),
            "bigint" => Simple(CanonicalTypeKind.Int64),

            "decimal" or "numeric" => new CanonicalType(
                CanonicalTypeKind.Decimal, null, CanonicalTypeSpec.IntAt(args, 0, 18), CanonicalTypeSpec.IntAt(args, 1, 0), false, false),

            // Money collapses into a plain fixed-point number like any other decimal — this is where
            // its currency semantics (rounding rules, display) are lost, before any target is chosen.
            "money" => new CanonicalType(CanonicalTypeKind.Decimal, null, 19, 4, false, false,
                SourceNote: "SQL Server 'money' carries currency semantics that a generic decimal does not; only its scale survives."),
            "smallmoney" => new CanonicalType(CanonicalTypeKind.Decimal, null, 10, 4, false, false,
                SourceNote: "SQL Server 'smallmoney' carries currency semantics that a generic decimal does not; only its scale survives."),

            "real" => Simple(CanonicalTypeKind.Float),
            "float" => Simple(CanonicalTypeKind.Double),

            "char" => StringType(args, unicode: false),
            "varchar" => StringType(args, unicode: false),
            "nchar" => StringType(args, unicode: true),
            "nvarchar" => StringType(args, unicode: true),
            "text" => new CanonicalType(CanonicalTypeKind.String, null, null, null, false, true),
            "ntext" => new CanonicalType(CanonicalTypeKind.String, null, null, null, true, true),

            "binary" => BinaryType(args, isFixed: true),
            "varbinary" => BinaryType(args, isFixed: false),
            "image" => new CanonicalType(CanonicalTypeKind.Binary, null, null, null, false, true),

            // rowversion is the current name; timestamp is the deprecated syntax for declaring the
            // same column, but TYPE_NAME() — what this app's own catalog queries report — always says
            // "timestamp" regardless of which one was written, so that's the name matched here. Always
            // exactly 8 bytes, auto-assigned by the engine on every write — replicating it as a plain
            // fixed-length binary(8) copies the value faithfully without a target column that tries
            // (and fails) to behave like a second engine-managed rowversion of its own.
            "timestamp" or "rowversion" => new CanonicalType(CanonicalTypeKind.Binary, 8, null, null, false, false, IsFixed: true),

            "date" => Simple(CanonicalTypeKind.Date),
            "time" => new CanonicalType(CanonicalTypeKind.Time, null, null, CanonicalTypeSpec.IntAt(args, 0, 7), false, false),
            "smalldatetime" => new CanonicalType(CanonicalTypeKind.Timestamp, null, null, 0, false, false),
            "datetime" => new CanonicalType(CanonicalTypeKind.Timestamp, null, null, 3, false, false),
            "datetime2" => new CanonicalType(CanonicalTypeKind.Timestamp, null, null, CanonicalTypeSpec.IntAt(args, 0, 7), false, false),
            "datetimeoffset" => new CanonicalType(CanonicalTypeKind.TimestampTz, null, null, CanonicalTypeSpec.IntAt(args, 0, 7), false, false),

            "uniqueidentifier" => Simple(CanonicalTypeKind.Guid),
            "xml" => Simple(CanonicalTypeKind.Xml),

            _ => Simple(CanonicalTypeKind.Unmappable),
        };

        static CanonicalType Simple(CanonicalTypeKind kind) => new(kind, null, null, null, false, false);

        static CanonicalType StringType(IReadOnlyList<string> args, bool unicode) =>
            args is ["max"]
                ? new CanonicalType(CanonicalTypeKind.String, null, null, null, unicode, true)
                : new CanonicalType(CanonicalTypeKind.String, CanonicalTypeSpec.IntAt(args, 0, 1), null, null, unicode, false);

        static CanonicalType BinaryType(IReadOnlyList<string> args, bool isFixed) =>
            args is ["max"]
                // varbinary(max) only — binary has no max form.
                ? new CanonicalType(CanonicalTypeKind.Binary, null, null, null, false, true)
                : new CanonicalType(
                    CanonicalTypeKind.Binary, CanonicalTypeSpec.IntAt(args, 0, 1), null, null, false, false,
                    IsFixed: isFixed);
    }

    public override RenderedColumnType RenderColumnType(CanonicalType type) => type.Kind switch
    {
        CanonicalTypeKind.Boolean => Faithful("bit", type),
        CanonicalTypeKind.Int8 => Faithful("tinyint", type),
        CanonicalTypeKind.Int16 => Faithful("smallint", type),
        CanonicalTypeKind.Int32 => Faithful("int", type),
        CanonicalTypeKind.Int64 => Faithful("bigint", type),
        CanonicalTypeKind.Decimal => Faithful($"decimal({type.Precision ?? 18},{type.Scale ?? 0})", type),
        CanonicalTypeKind.Float => Faithful("real", type),
        CanonicalTypeKind.Double => Faithful("float", type),

        CanonicalTypeKind.String when type.IsMax =>
            Faithful(type.IsUnicode ? "nvarchar(max)" : "varchar(max)", type),
        CanonicalTypeKind.String =>
            Faithful($"{(type.IsUnicode ? "nvarchar" : "varchar")}({type.Length ?? 255})", type),

        CanonicalTypeKind.Binary when type.IsMax => Faithful("varbinary(max)", type),
        CanonicalTypeKind.Binary when type.IsFixed => Faithful($"binary({type.Length ?? 255})", type),
        CanonicalTypeKind.Binary => Faithful($"varbinary({type.Length ?? 255})", type),

        CanonicalTypeKind.Date => Faithful("date", type),
        CanonicalTypeKind.Time => Faithful($"time({type.Scale ?? 7})", type),
        CanonicalTypeKind.Timestamp => Faithful($"datetime2({type.Scale ?? 7})", type),
        CanonicalTypeKind.TimestampTz => Faithful("datetimeoffset", type),
        CanonicalTypeKind.Guid => Faithful("uniqueidentifier", type),
        CanonicalTypeKind.Xml => Faithful("xml", type),

        // SQL Server has no native JSON type — this is the reverse-direction case phase 25 calls out
        // by name (Postgres jsonb -> nvarchar(max)).
        CanonicalTypeKind.Json => new RenderedColumnType(
            "nvarchar(max)", "SQL Server has no native JSON type; stored as nvarchar(max) text."),

        CanonicalTypeKind.Unmappable => throw new InvalidOperationException(
            "RenderColumnType must never be called for Unmappable — the caller checks CanonicalType.Kind " +
            "first and reports the plan as Unsupported instead."),

        _ => throw new ArgumentOutOfRangeException(nameof(type), type.Kind, "Unknown canonical type kind."),
    };

    /// <summary>Combines whatever the *source* side already knew (<see cref="CanonicalType.SourceNote"/>)
    /// with anything the render itself finds — so a caller only ever reads one fidelity string rather
    /// than checking two places.</summary>
    private static RenderedColumnType Faithful(string sql, CanonicalType type) => new(sql, type.SourceNote);
}
