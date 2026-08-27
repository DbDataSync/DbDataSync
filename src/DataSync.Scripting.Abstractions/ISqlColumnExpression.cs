using DataSync.Drivers.Abstractions;

namespace DataSync.Scripting.Abstractions;

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
/// The column's metadata as the source catalog reports it, or null when the catalog was not consulted —
/// a reader that projects without reading metadata first still has to be able to call this.
/// </param>
/// <param name="ColumnReference">
/// The column, already quoted and already qualified **for the statement being built**. This is the
/// string a literal transform's <c>{{column}}</c> is replaced with, and for the same reason: the
/// Change Tracking reader's statement joins the source table under an alias, so the correct reference
/// there is <c>base.[Region]</c> and not <c>[Region]</c>. A script that builds its own reference from
/// the column name will be wrong in exactly that reader.
/// </param>
public sealed record SqlColumnExpressionContext(
    string SourceColumn,
    string TargetColumn,
    ColumnMetadata? Column,
    string ColumnReference,
    IScriptDialect Dialect,
    ScriptParameters Parameters);
