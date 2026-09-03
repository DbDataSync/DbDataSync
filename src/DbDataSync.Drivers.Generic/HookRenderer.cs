using DbDataSync.Core.Config;
using DbDataSync.Drivers.Abstractions;
using DbDataSync.Core.Sql;

namespace DbDataSync.Drivers.Generic;

/// <summary>
/// Everything a hook body can reference at render time — the identifiers behind the five built-in
/// tokens, already qualified where that means something, and the values behind the eleven built-in
/// parameters. Built fresh at each call site with whatever is known *then*: <see cref="StagingQualified"/>
/// is null before staging exists, <see cref="RowsWritten"/> is null before the load — nullable rather
/// than a placeholder, because "none" and "not yet known" are different answers a hook may need to
/// tell apart.
/// </summary>
public sealed record HookRenderContext(
    string TargetQualified,
    string TargetSchemaQuoted,
    string TargetTableQuoted,
    string SourceQualified,
    string? StagingQualified,
    string Replication,
    string Mapping,
    Guid RunId,
    string RunKind,
    string? Segment,
    int SegmentIndex,
    int SegmentCount,
    bool IsLastSegment,
    long? RowsStaged,
    long? RowsWritten,
    string? Watermark);

/// <summary>
/// Turns a hook body — inline SQL, or a reusable hook's code plus the values a binding supplied for its
/// declared parameters — into a <see cref="HookStatement"/>: every <c>{{token}}</c> replaced with a
/// quoted identifier, every referenced <c>@parameter</c> rendered in the target dialect's own
/// placeholder syntax and bound rather than interpolated. The same split <c>StagingStatement</c> and
/// <c>ScriptedColumnTransforms</c> use — a renderer that produces text, kept separate from whatever
/// executes it.
/// </summary>
public static class HookRenderer
{
    public static HookStatement Render(
        SqlDialect dialect, string sql, IReadOnlyDictionary<string, string> declaredParameterValues, HookRenderContext context)
    {
        var withTokens = SubstituteTokens(dialect, sql, declaredParameterValues, context);
        return SubstituteParameters(dialect, withTokens, context);
    }

    private static string SubstituteTokens(
        SqlDialect dialect, string sql, IReadOnlyDictionary<string, string> declaredParameterValues, HookRenderContext context)
    {
        var text = sql;
        text = ReplaceToken(text, "target", context.TargetQualified);
        text = ReplaceToken(text, "targetSchema", context.TargetSchemaQuoted);
        text = ReplaceToken(text, "targetTable", context.TargetTableQuoted);
        text = ReplaceToken(text, "source", context.SourceQualified);

        if (context.StagingQualified is not null)
            text = ReplaceToken(text, "staging", context.StagingQualified);

        foreach (var (name, value) in declaredParameterValues)
            text = ReplaceToken(text, name, QuoteMaybeQualified(dialect, value));

        return text;
    }

    private static string ReplaceToken(string text, string name, string quotedValue)
    {
        var token = $"{{{{{name}}}}}";
        return text.Contains(token, StringComparison.Ordinal) ? text.Replace(token, quotedValue, StringComparison.Ordinal) : text;
    }

    /// <summary>
    /// A declared parameter is an identifier, but the value an operator supplies for one is sometimes a
    /// qualified name (phase 26's own example: <c>controlTable: dbo.LoadControl</c>) and sometimes a
    /// bare one. Both spellings work without the operator pre-quoting anything.
    /// <para>
    /// The split is <see cref="SqlDialect.SplitQualifiedName"/>'s, which respects the dialect's own
    /// quoting rather than cutting on every dot. That matters because a hook parameter is free text —
    /// there is no structured schema-and-table pair to carry instead, which is the fix the plan
    /// preferred and this value's shape rules out. So the rule is stated instead: a name containing a
    /// literal dot is written quoted (<c>[dbo].[My.Table]</c>), and an unquoted <c>My.Table</c> means
    /// schema <c>My</c> — the reading it has always had, and the only one available without asking.
    /// </para>
    /// <para>
    /// Three or more parts are quoted whole, unchanged from before: a cross-database reference is not
    /// something this substitution has ever claimed to handle, and guessing which part is the database
    /// would be inventing an answer rather than declining to.
    /// </para>
    /// </summary>
    private static string QuoteMaybeQualified(SqlDialect dialect, string value)
    {
        var parts = dialect.SplitQualifiedName(value);
        return parts.Count switch
        {
            1 => dialect.QuoteIdentifier(parts[0]),
            2 => dialect.QualifyTable(parts[0], parts[1]),
            _ => dialect.QuoteIdentifier(value),
        };
    }

    private static HookStatement SubstituteParameters(SqlDialect dialect, string text, HookRenderContext context)
    {
        var referenced = HookValidation.FindParameterReferences(text);
        var parameters = new List<HookParameter>();

        foreach (var name in referenced)
        {
            if (!HookValidation.BuiltInParameters.Contains(name, StringComparer.Ordinal))
                continue; // Already rejected at save time by HookValidation; defensive rather than load-bearing here.

            text = text.Replace($"@{name}", dialect.ParameterReference(name), StringComparison.Ordinal);
            parameters.Add(new HookParameter(name, ValueOf(name, context) ?? DBNull.Value));
        }

        return new HookStatement(text, parameters);
    }

    private static object? ValueOf(string parameterName, HookRenderContext c) => parameterName switch
    {
        "replication" => c.Replication,
        "mapping" => c.Mapping,
        "runId" => c.RunId,
        "runKind" => c.RunKind,
        "segment" => c.Segment,
        "segmentIndex" => c.SegmentIndex,
        "segmentCount" => c.SegmentCount,
        "isLastSegment" => c.IsLastSegment,
        "rowsStaged" => c.RowsStaged,
        "rowsWritten" => c.RowsWritten,
        "watermark" => c.Watermark,
        _ => throw new InvalidOperationException($"Unknown hook parameter '{parameterName}'."),
    };
}
