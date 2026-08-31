using System.Data.Common;
using System.Globalization;

namespace DataSync.Core.Sql;

/// <summary>
/// One runtime value a previewed statement would bind, rendered as it would actually be bound right
/// now — not the bare placeholder the statement text shows in its place.
/// </summary>
/// <param name="Name">Without the dialect's sigil — the same name passed to <see
/// cref="SqlDialect.ParameterReference"/> when the statement itself was built.</param>
/// <param name="SqlType">A type an admin's query tool will accept in a declaration for this value —
/// the source column's own native type where there is one, a fixed type where the value is always the
/// same shape (an LSN, a change-tracking version).</param>
/// <param name="Literal">Already rendered via <see cref="SqlDialect.RenderLiteral"/> — quoted, escaped
/// or hex-prefixed as this value's type needs.</param>
public sealed record PreviewParameter(string Name, string SqlType, string Literal);

/// <summary>
/// The small, mechanical ways SQL engines disagree — quoting, parameter placeholders, switching the
/// current database — so that a statement whose *shape* is identical everywhere does not need one
/// copy per engine.
/// <para>
/// This is deliberately not an attempt to abstract over engines. Anything that differs structurally —
/// bulk loading, upsert syntax, identity handling, catalog queries — belongs in an engine-specific
/// implementation with a prefixed Kind. If generalising something here would need a flag per engine,
/// that is the signal it does not belong here.
/// </para>
/// </summary>
public abstract class SqlDialect
{
    /// <summary>Quotes a table, schema or column name. Identifiers reach this from introspected
    /// metadata or validated config, never raw end-user input (architecture/detailed-design.md §6);
    /// implementations still escape the closing delimiter so a name containing one cannot break out.</summary>
    public abstract string QuoteIdentifier(string identifier);

    /// <summary>How a parameter is referenced *in statement text*: <c>@p</c> on SQL Server and MySQL,
    /// <c>:p</c> on Oracle.</summary>
    public abstract string ParameterReference(string name);

    /// <summary>What <see cref="DbParameter.ParameterName"/> must be set to for the same parameter.
    /// Usually identical to <see cref="ParameterReference"/>; some providers want the bare name
    /// without its sigil, which is why the two are separate.</summary>
    public virtual string ParameterName(string name) => ParameterReference(name);

    public virtual string QualifyTable(string schema, string table) =>
        string.IsNullOrEmpty(schema) ? QuoteIdentifier(table) : $"{QuoteIdentifier(schema)}.{QuoteIdentifier(table)}";

    /// <summary>The characters this engine wraps an identifier in. ANSI double quotes by default;
    /// SQL Server's brackets are the divergence.</summary>
    protected virtual char IdentifierQuoteOpen => '"';

    protected virtual char IdentifierQuoteClose => '"';

    /// <summary>
    /// Splits an operator-typed name into its parts on its **unquoted** dots, unwrapping each part.
    /// <para>
    /// <c>dbo.LoadControl</c> is two parts, and so is <c>[dbo].[LoadControl]</c>. A dot *inside* the
    /// quoting is part of the name: <c>[dbo].[My.Table]</c> is still two parts, the second being
    /// <c>My.Table</c>. That is the rule this method exists to state, because a bare
    /// <c>My.Table</c> is genuinely ambiguous — it reads as schema <c>My</c>, table <c>Table</c>, and
    /// no amount of cleverness here can tell it from a table whose name contains a dot. Quoting is
    /// how the operator says which one they meant; splitting blindly on every dot took that away.
    /// </para>
    /// <para>
    /// A doubled closing quote is an escaped one (<c>[a]]b]</c>, <c>"a""b"</c>), as in every engine
    /// that quotes this way.
    /// </para>
    /// </summary>
    public IReadOnlyList<string> SplitQualifiedName(string value)
    {
        var parts = new List<string>();
        var current = new System.Text.StringBuilder();
        var quoted = false;

        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];

            if (!quoted && c == IdentifierQuoteOpen)
            {
                quoted = true;
                continue;
            }

            if (quoted && c == IdentifierQuoteClose)
            {
                if (i + 1 < value.Length && value[i + 1] == IdentifierQuoteClose)
                {
                    current.Append(IdentifierQuoteClose);
                    i++;
                    continue;
                }

                quoted = false;
                continue;
            }

            if (!quoted && c == '.')
            {
                parts.Add(current.ToString());
                current.Clear();
                continue;
            }

            current.Append(c);
        }

        parts.Add(current.ToString());
        return parts;
    }

    /// <summary>
    /// Points an open connection at <paramref name="database"/>. Virtual because "database" is not
    /// universal: engines where a connection cannot change database (Oracle, where the schema is the
    /// unit) override this to validate-and-ignore rather than call
    /// <see cref="DbConnection.ChangeDatabase"/>, which they throw from.
    /// </summary>
    public virtual Task UseDatabaseAsync(DbConnection connection, string database, CancellationToken cancellationToken)
    {
        connection.ChangeDatabase(database);
        return Task.CompletedTask;
    }

    /// <summary>
    /// The most parameters one statement may carry. SQL Server caps a request at 2100; Postgres and
    /// MySQL allow 65535. The default is deliberately the *lowest* of the engines in scope, because a
    /// dialect that forgets to state its own limit should batch too conservatively rather than emit a
    /// statement the server rejects.
    /// </summary>
    public virtual int MaxParametersPerStatement => 2100;

    /// <summary>Column type for the staging table's operation marker. One character on every engine in
    /// scope, but spelled as a type name, which is the part that varies.</summary>
    public virtual string OperationMarkerColumnType => "CHAR(1)";

    /// <summary>
    /// The two fragments that cap an ordered query at a row count *without splitting ties*: whatever
    /// goes right after <c>SELECT</c>, and whatever goes at the end of the statement. One of the two is
    /// always empty, because the engines in scope put the clause at opposite ends.
    /// <para>
    /// Tie-safety is the whole point, not a refinement. The position a bounded read records is its last
    /// row's ordering value; if a sibling row sharing that exact value were left behind, the next pass —
    /// which reads strictly greater than the recorded position — would skip it forever. <c>WITH TIES</c>
    /// is the engine promising that cannot happen, which is cheaper and far more trustworthy than
    /// negotiating a boundary in application code.
    /// </para>
    /// <para>
    /// SQL-standard <c>FETCH FIRST … ROWS WITH TIES</c> by default (Postgres 13+, Oracle 12c+); SQL
    /// Server spells it <c>TOP (n) WITH TIES</c> at the front and overrides.
    /// </para>
    /// </summary>
    public virtual (string Prefix, string Suffix) RenderTieSafeRowLimit(string parameterName) =>
        ("", $"\nFETCH FIRST {ParameterReference(parameterName)} ROWS WITH TIES");

    /// <summary>
    /// The staging table's ordinal column, definition and all: an engine-assigned, monotonically
    /// increasing number per staged row, which is what a chunked apply ranges over.
    /// <para>
    /// Rendered whole rather than as a type name because the three parts that matter — how the engine
    /// spells "generate this for me", and that the column is the table's key so a chunk's range is a
    /// seek rather than a scan — are not separable. A chunked apply that had to scan the staging table
    /// once per chunk would be quadratic, which would make chunking cost more than it saved.
    /// </para>
    /// <para>
    /// ANSI <c>GENERATED ALWAYS AS IDENTITY</c> by default (Postgres, and the standard); SQL Server
    /// spells it <c>IDENTITY(1,1)</c> and overrides.
    /// </para>
    /// </summary>
    public virtual string RenderStagingOrdinalColumn(string column) =>
        $"{QuoteIdentifier(column)} BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY";

    /// <summary>Renders a multi-row insert. The <c>VALUES (…), (…)</c> form works on SQL Server 2008+,
    /// Postgres and MySQL; Oracle's <c>INSERT ALL</c> is a different statement entirely, which is why
    /// this is a hook rather than a format string.</summary>
    /// <param name="rowValueTuples">Each already rendered as <c>(@p0_0, @p0_1, …)</c>.</param>
    public virtual string RenderMultiRowInsert(string qualifiedTable, string columnList, IReadOnlyList<string> rowValueTuples) =>
        $"INSERT INTO {qualifiedTable} ({columnList}) VALUES {string.Join(", ", rowValueTuples)};";

    /// <summary>Drops a table if it is there. <c>IF EXISTS</c> is not universal (Oracle needs a PL/SQL
    /// block around the drop), so the whole statement is the hook.</summary>
    /// <summary>
    /// A handful of rows from a table, for looking at rather than for moving. Used by phase 41's live
    /// script test, which reads real rows so an operator can see what their transform does to them.
    /// <para>
    /// <c>LIMIT</c> by default because most engines have it; SQL Server does not and overrides with
    /// <c>TOP</c>. No ordering: an unordered sample is honest about being a sample, and adding one
    /// would mean choosing a column and paying for a sort against a table that may be enormous.
    /// </para>
    /// </summary>
    /// <summary>
    /// Add a column to an existing table. <c>ADD</c> on most engines; SQL Server spells it
    /// <c>ADD</c> too but without the <c>COLUMN</c> keyword, so this is virtual rather than shared.
    /// <para>
    /// The column is added nullable regardless of what the mapping says, and that is not laziness: a
    /// table with rows in it cannot gain a <c>NOT NULL</c> column without a default, and inventing a
    /// default for somebody's data is exactly the kind of decision this system does not make.
    /// </para>
    /// </summary>
    public virtual string RenderAddColumn(string qualifiedTable, string column, string type) =>
        $"ALTER TABLE {qualifiedTable} ADD COLUMN {QuoteIdentifier(column)} {type} NULL;";

    /// <summary>
    /// Change an existing column's type, or null when this engine cannot express the change as a
    /// single statement — in which case the plan reports it as unsupported and names the column,
    /// rather than emitting something that might silently truncate.
    /// </summary>
    public virtual string? RenderAlterColumnType(string qualifiedTable, string column, string type) =>
        $"ALTER TABLE {qualifiedTable} ALTER COLUMN {QuoteIdentifier(column)} TYPE {type};";

    /// <summary>
    /// Rename an existing column, keeping its type and its data.
    /// <para>
    /// A rename rather than an add-and-copy because the point of recording one is that the target's
    /// existing rows keep their values: dropping the old column is not something provisioning does,
    /// and leaving it beside the new one would leave the table with two columns and no way to tell
    /// which is current.
    /// </para>
    /// </summary>
    public virtual string RenderRenameColumn(string qualifiedTable, string from, string to) =>
        $"ALTER TABLE {qualifiedTable} RENAME COLUMN {QuoteIdentifier(from)} TO {QuoteIdentifier(to)};";

    /// <summary>
    /// An expression rendered as text, for building a composite value out of columns of mixed type.
    /// ANSI <c>CAST(… AS VARCHAR(…))</c> by default; engines that spell it differently override.
    /// </summary>
    public virtual string CastToText(string expression) => $"CAST({expression} AS VARCHAR(4000))";

    /// <summary>
    /// How this engine writes a boolean literal in SQL.
    /// <para>
    /// Not cosmetic: a canonical <c>Boolean</c> renders as <c>boolean</c> on Postgres and <c>bit</c> on
    /// SQL Server, and <c>= 1</c> against the former is "operator does not exist: boolean = integer".
    /// A statement builder that hardcodes either one works on exactly one engine.
    /// </para>
    /// </summary>
    public virtual string TrueLiteral => "TRUE";

    public virtual string FalseLiteral => "FALSE";

    /// <summary>
    /// Joins string expressions. ANSI <c>||</c> by default; SQL Server spells it <c>+</c>, which is
    /// the other half of the same problem.
    /// </summary>
    public virtual string Concat(IEnumerable<string> expressions) => string.Join(" || ", expressions);

    /// <summary>
    /// Renders one bound value as a SQL literal — for a preview that shows exactly what a statement
    /// would run, not a bare parameter placeholder standing in for a value nobody can see.
    /// <para>
    /// ANSI-ish default: quoted and doubled-quote-escaped for text, hex-prefixed for binary, ISO-ish
    /// for dates. A dialect overrides only where its literal syntax genuinely differs.
    /// </para>
    /// </summary>
    public virtual string RenderLiteral(object? value) => value switch
    {
        null or DBNull => "NULL",
        bool b => b ? TrueLiteral : FalseLiteral,
        byte[] bytes => "0x" + Convert.ToHexString(bytes),
        DateTime dt => $"'{dt:yyyy-MM-dd HH:mm:ss.fffffff}'",
        DateTimeOffset dto => $"'{dto:yyyy-MM-dd HH:mm:ss.fffffff zzz}'",
        Guid g => $"'{g}'",
        string s => $"'{s.Replace("'", "''")}'",
        _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? "NULL",
    };

    /// <summary>
    /// Declares each parameter as a variable ahead of the statement that references it, so pasting
    /// both into a query tool reproduces exactly what a pass would run right now — instead of the
    /// placeholder the statement text shows, which no query tool can resolve on its own.
    /// <para>
    /// Null when this engine has no notion of a variable outside a query itself, which is reported to
    /// an operator as that rather than papered over with something that would not actually run.
    /// </para>
    /// </summary>
    public virtual string? RenderDeclarations(IReadOnlyList<PreviewParameter> parameters) => null;

    public virtual string RenderSampleSelect(string qualifiedTable, int rows) =>
        $"SELECT * FROM {qualifiedTable} LIMIT {rows};";

    public virtual string RenderDropTableIfExists(string qualifiedTable) =>
        $"DROP TABLE IF EXISTS {qualifiedTable};";

    /// <summary>
    /// How a column's type divides into buckets for auto-segmentation. The default covers the type
    /// names the ISO SQL types share; a dialect adds its own spellings on top (SQL Server's
    /// <c>datetime2</c> and <c>money</c>, Postgres's <c>timestamptz</c>) and should call
    /// <c>base</c> for anything it does not recognise.
    /// </summary>
    public virtual BucketableKind ClassifyForBucketing(string baseTypeName) => baseTypeName switch
    {
        "tinyint" or "smallint" or "int" or "integer" or "bigint" => BucketableKind.Integral,
        "decimal" or "numeric" or "float" or "real" or "double" or "double precision" => BucketableKind.Numeric,
        "date" or "datetime" or "timestamp" => BucketableKind.DateTime,
        _ => BucketableKind.NotBucketable,
    };

    /// <summary>
    /// Renders the <c>INSERT INTO … (columns)</c> head, given whether the statement supplies explicit
    /// values for a generated column. Postgres needs <c>OVERRIDING SYSTEM VALUE</c> *inside* the
    /// statement; SQL Server needs a session flag around it, and so ignores the flag here and uses
    /// <see cref="WriteWithGeneratedColumnOverrideAsync"/> instead. Both hooks exist because the two
    /// engines put the same intent in different places.
    /// </summary>
    public virtual string RenderInsertInto(string qualifiedTable, string columnList, bool overrideGenerated) =>
        $"INSERT INTO {qualifiedTable} ({columnList})";

    /// <summary>
    /// Runs <paramref name="write"/> with whatever the engine needs in order to accept explicit values
    /// for generated columns. SQL Server brackets it with <c>SET IDENTITY_INSERT</c>, Postgres uses
    /// <c>OVERRIDING SYSTEM VALUE</c> on the statement itself, MySQL needs nothing — the three have
    /// their purpose in common and nothing else, so the hook is "run this write" rather than "give me
    /// a clause".
    /// </summary>
    public virtual Task<T> WriteWithGeneratedColumnOverrideAsync<T>(
        DbConnection connection,
        DbTransaction? transaction,
        string qualifiedTable,
        bool overrideRequired,
        Func<Task<T>> write,
        CancellationToken cancellationToken) => write();

    /// <summary>
    /// Translates one of this engine's native type specs (e.g. <c>"nvarchar(50)"</c>, the exact string
    /// <see cref="Abstractions.ColumnMetadata.NativeType"/> carries) into the canonical intermediate a
    /// provisioning plan uses to create a matching column on a *different* engine. See phase 25 —
    /// <c>architecture/implementation/todo/phase-025-database-provisioning.md</c>.
    /// </summary>
    public abstract CanonicalType ToCanonicalType(string nativeType);

    /// <summary>
    /// The reverse direction: renders this engine's DDL for a canonical type produced by (usually)
    /// another engine's <see cref="ToCanonicalType"/>. Must not be called with
    /// <see cref="CanonicalTypeKind.Unmappable"/> — a caller checks <see cref="CanonicalType.Kind"/>
    /// and reports <c>Unsupported</c> before ever reaching a renderer.
    /// </summary>
    public abstract RenderedColumnType RenderColumnType(CanonicalType type);
}

/// <summary>How auto-segmentation may divide a column's value space. Not a type system — only the four
/// arithmetics <see cref="SegmentExpansion"/> knows how to bucket.</summary>
public enum BucketableKind
{
    NotBucketable,
    Integral,
    Numeric,
    DateTime,
    DateTimeOffset,
}
