using DbDataSync.Core.Config;
using DbDataSync.Drivers.Abstractions;
using DbDataSync.Scripting.Abstractions;

namespace DbDataSync.Scripting;

/// <summary>
/// Runs a bound <see cref="ISqlColumnExpression"/> over a mapping's columns and returns a new column
/// list with the generated SQL sitting in <see cref="ColumnMapping.Transform"/>.
/// <para>
/// **The script produces the same thing a human types, and then nothing downstream knows the
/// difference.** A generated expression goes through phase 22's projection exactly as a hand-written
/// one does, including <c>{{column}}</c> substitution — which is what makes it correct in the Change
/// Tracking reader, whose statement aliases the source table. Had the script been called from inside
/// the reader instead, there would be two paths to the same feature and the generated one would be the
/// one nobody could see.
/// </para>
/// <para>
/// It also means the script runs once per pass rather than once per statement, and that the SQL it
/// generated can be logged and read back by the operator.
/// </para>
/// </summary>
public static class ScriptedColumnTransforms
{
    /// <summary>
    /// Returns <paramref name="columnMappings"/> unchanged when nothing is bound or nothing is
    /// generated, so the common case allocates nothing and the caller needs no branch.
    /// </summary>
    /// <param name="log">Called once per column a script actually generated something for, with the
    /// source column's name and the SQL generated for it — structured rather than a single pre-joined
    /// line, so a caller with a hundred-plus columns to report (<c>PreviewService</c>'s own "Generated
    /// column expressions" table) can lay them out as data instead of parsing one back out of text.</param>
    public static IReadOnlyList<ColumnMapping> Apply(
        IReadOnlyList<ColumnMapping> columnMappings,
        ISqlColumnExpression? expression,
        ScriptParameters parameters,
        IScriptDialect dialect,
        IReadOnlyList<ColumnMetadata>? columnMetadata = null,
        Action<string, string>? log = null)
    {
        if (expression is null || columnMappings.Count == 0)
            return columnMappings;

        List<ColumnMapping>? result = null;

        for (var i = 0; i < columnMappings.Count; i++)
        {
            var mapping = columnMappings[i];

            // A literal transform wins. A value typed into that row of the editor is the more specific
            // statement of intent, and it is the one the operator can see.
            if (!string.IsNullOrWhiteSpace(mapping.Transform))
                continue;

            var column = columnMetadata?.FirstOrDefault(
                c => string.Equals(c.Name, mapping.SourceColumn, StringComparison.OrdinalIgnoreCase));

            string? generated;
            try
            {
                generated = expression.RenderSql(new SqlColumnExpressionContext(
                    mapping.SourceColumn,
                    mapping.TargetColumn,
                    column,
                    // The token, not a resolved reference: the script emits an expression in exactly
                    // the form a human would type, and the reader substitutes its own qualification.
                    ColumnMapping.ColumnToken,
                    dialect,
                    parameters));
            }
            catch (Exception ex) when (ex is not ScriptExecutionException)
            {
                throw new ScriptExecutionException(
                    $"The column-expression script threw while generating SQL for '{mapping.SourceColumn}': {ex.Message}", ex);
            }

            if (string.IsNullOrWhiteSpace(generated))
                continue;

            result ??= [.. columnMappings];
            result[i] = new ColumnMapping
            {
                SourceColumn = mapping.SourceColumn,
                TargetColumn = mapping.TargetColumn,
                Transform = generated,
            };
            log?.Invoke(mapping.SourceColumn, generated);
        }

        return result ?? columnMappings;
    }
}

