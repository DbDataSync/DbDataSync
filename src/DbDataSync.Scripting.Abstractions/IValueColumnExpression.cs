using DbDataSync.Core.Config;
using DbDataSync.Drivers.Abstractions;

namespace DbDataSync.Scripting.Abstractions;

/// <summary>
/// Transforms one column's value in this process, per cell, as rows flow from the reader to staging.
/// <para>
/// The middle of the three transform costs. <see cref="ISqlColumnExpression"/> is free to us because
/// the source evaluates it; this one buys the whole of C# and pays a delegate call for every cell of
/// every column it declares. Use it where the source's SQL genuinely cannot express the thing.
/// </para>
/// </summary>
public interface IValueColumnExpression
{
    /// <summary>
    /// Called **once per pass**, before any row. The source columns this transforms; every other column
    /// is passed through untouched and never reaches <see cref="Evaluate"/>.
    /// <para>
    /// Not ceremony. Without it, a transform interested in one column of forty costs a delegate call on
    /// all forty for every row — the same per-cell cost phase 14's positional row layout exists to
    /// avoid. Declaring narrows the hot path to an ordinal check.
    /// </para>
    /// </summary>
    IReadOnlyList<string> DeclareColumns(ValueColumnDeclarationContext context);

    object? Evaluate(object? value, ValueColumnExpressionContext context);
}

public sealed record ValueColumnDeclarationContext(
    IReadOnlyList<ColumnMapping> ColumnMappings,
    IScriptDialect Dialect,
    ScriptParameters Parameters);

public sealed record ValueColumnExpressionContext(
    string SourceColumn,
    string TargetColumn,
    IScriptDialect Dialect,
    ScriptParameters Parameters);
