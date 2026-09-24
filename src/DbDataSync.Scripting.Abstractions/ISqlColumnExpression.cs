using DbDataSync.Drivers.Abstractions;

namespace DbDataSync.Scripting.Abstractions;

/// <summary>
/// Generates a SQL expression, in the *source's* dialect, for one column of a table mapping. The source
/// engine evaluates it; this process evaluates nothing.
/// <para>
/// The cheapest of the transform slots by a wide margin, and the reason it is the first one built: a
/// script runs once per pass to produce a string, and the source then applies that string to a billion
/// rows for free. Compare <c>IValueColumnExpression</c> (a delegate call per cell) and
/// <c>IRowTransform</c> (per row), which buy the full expressiveness of C# at run time and pay for it.
/// </para>
/// </summary>
public interface ISqlColumnExpression
{
    /// <summary>Return null to leave the column exactly as it would have been.</summary>
    string? RenderSql(SqlColumnExpressionContext context);
}

/// <param name="Column">
/// The column's *cached* metadata — the mapping's own <c>SourceColumns</c> cache for a primary-sourced
/// column, or the matching <c>RelationshipColumns</c> entry when this mapping is relationship-sourced —
/// or null when nothing cached matches. Never a live catalog call: a run operates on cached metadata
/// only, the same as every other reader/writer consumer of it.
/// </param>
/// <param name="ColumnReference">
/// **Not a resolved reference — always the literal <c>{{column}}</c> token**, substituted later by
/// whatever projection renders the actual statement. The Change Tracking reader's statement joins the
/// source table under an alias, so the reference it substitutes there is <c>base.[Region]</c> and not
/// <c>[Region]</c>; a script that builds its own reference from the column name instead of returning an
/// expression built around this token will be wrong in exactly that reader.
/// </param>
public sealed record SqlColumnExpressionContext(
    string SourceColumn,
    string TargetColumn,
    ColumnMetadata? Column,
    string ColumnReference,
    IScriptDialect Dialect,
    ScriptParameters Parameters);
