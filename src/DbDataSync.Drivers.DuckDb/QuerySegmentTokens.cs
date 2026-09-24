using DbDataSync.Core.Config;
using DbDataSync.Core.Sql;

namespace DbDataSync.Drivers.DuckDb;

/// <summary>
/// DuckDB's own segment-token substitution — now a thin wrapper over the engine-neutral
/// <see cref="DbDataSync.Drivers.Generic.QuerySegmentTokens"/>, which a raw-query source generalized
/// to every driver. Kept as its own type, with its own two-argument signature, purely for this
/// project's existing tests and for anything outside this codebase that might reference it by name;
/// see that type's own doc comment for the actual mechanics.
/// </summary>
public static class QuerySegmentTokens
{
    public const string Column = Generic.QuerySegmentTokens.Column;
    public const string Min = Generic.QuerySegmentTokens.Min;
    public const string Max = Generic.QuerySegmentTokens.Max;
    public const string Values = Generic.QuerySegmentTokens.Values;

    public static string Substitute(string query, BatchReloadSegment? segment) =>
        Generic.QuerySegmentTokens.Substitute(query, segment, DuckDbDialect.Instance, DuckDbQueryReader.ReaderKind);

    /// <summary>A single-quoted SQL string literal, with embedded quotes doubled.</summary>
    public static string Literal(string value) => Generic.QuerySegmentTokens.Literal(value);
}
