using DbDataSync.Core.Config;
using DbDataSync.Core.Sql;

namespace DbDataSync.Drivers.DuckDb;

/// <summary>
/// Puts a <see cref="BatchReloadSegment"/>'s bounds into an operator's own query text.
/// <para>
/// **This is not predicate generation, and the difference is the whole design.** Every other segmented
/// reader in this repo builds its own <c>WHERE</c> clause — <c>SegmentScope</c> renders a comparison
/// against a bound parameter, having asked a catalog what type the column is, because it is composing
/// SQL around a table an operator only named. Here the operator wrote the entire statement, so there
/// is nothing to compose around and nowhere to put a clause they did not ask for. What a segment
/// supplies instead is three values, and where they go is the query author's decision:
/// </para>
/// <code>
/// SELECT * FROM read_parquet('s3://orders/*.parquet')
/// WHERE {{segmentColumn}} &gt;= {{segmentMin}} AND {{segmentColumn}} &lt; {{segmentMax}}
/// </code>
/// <para>
/// Substituted as *literals*, not as bound parameters. A parameter cannot be bound into a statement
/// whose shape the reader does not know — the operator may have written the token inside a
/// <c>read_csv</c> path, a <c>QUALIFY</c>, or nothing at all — and DuckDB casts a string literal to
/// the column's own type on comparison, so a single quoted form is correct for an integer, a date and
/// a string alike, without the column-type introspection this reader deliberately does not do. Both
/// halves are escaped here (<see cref="Literal"/>, <see cref="SqlDialect.QuoteIdentifier"/>) rather
/// than pasted raw: segment bounds come from a mapping's config, and config is a place a value with a
/// quote in it arrives by accident rather than by attack, but the failure is the same either way.
/// </para>
/// </summary>
public static class QuerySegmentTokens
{
    public const string Column = "{{segmentColumn}}";
    public const string Min = "{{segmentMin}}";
    public const string Max = "{{segmentMax}}";
    public const string Values = "{{segmentValues}}";

    /// <summary>
    /// <paramref name="query"/> with whatever <paramref name="segment"/> carries substituted in.
    /// <para>
    /// An unsegmented read — null, or <see cref="FullSegment"/> — returns the query **untouched**,
    /// tokens and all, rather than blanking them. A query written for segmenting and run without one
    /// is a mistake worth failing on: DuckDB rejects <c>{{segmentColumn}}</c> as a parse error naming
    /// the token, which is a far better thing for an operator to read than the silently different
    /// result set that erasing it would produce.
    /// </para>
    /// </summary>
    public static string Substitute(string query, BatchReloadSegment? segment) => segment switch
    {
        null or FullSegment => query,

        RangeSegment range => query
            .Replace(Column, Identifier(range.Column))
            .Replace(Min, Literal(range.RangeMin))
            .Replace(Max, Literal(range.RangeMax)),

        ListSegment list => query
            .Replace(Column, Identifier(list.Column))
            .Replace(Values, string.Join(", ", list.Values.Select(Literal))),

        // AutoSegment and CustomSegment are markers resolved before dispatch and never reach a runtime
        // reader (see BatchReloadSegment's own doc comment). Reached anyway, this says which one
        // arrived rather than substituting nothing and reading a different set of rows than the
        // segment described.
        _ => throw new InvalidOperationException(
            $"A '{segment.GetType().Name}' is not a runtime segment — it is expanded before a reader is " +
            $"handed one. The '{DuckDbQueryReader.ReaderKind}' reader received {segment.Describe()}."),
    };

    /// <summary>A single-quoted SQL string literal, with embedded quotes doubled.</summary>
    public static string Literal(string value) => $"'{value.Replace("'", "''")}'";

    private static string Identifier(string name) => DuckDbDialect.Instance.QuoteIdentifier(name);
}
