using DataSync.Core.Config;

namespace DataSync.Drivers.Generic;

/// <summary>
/// Renders a reader's SELECT list from the mapping's columns, applying each one's
/// <see cref="ColumnMapping.Transform"/> — a SQL expression in the *source* dialect, evaluated by the
/// source engine rather than by this process.
/// <para>
/// Every entry is aliased back to the **source** column name, so nothing downstream changes: staging
/// still maps source names to target names, and <c>ChangeSchema</c> still carries source names. A
/// transform replaces a column's value, never its identity.
/// </para>
/// </summary>
public static class SourceProjection
{
    /// <summary>The token a transform uses to refer to the column it is transforming. Substituted with
    /// a reference that is correct for the statement being built — which is not the same string in
    /// every reader, and is why this is a token rather than the bare column name. See
    /// <see cref="Render"/>.</summary>
    public const string ColumnToken = "{{column}}";

    /// <summary>
    /// The SELECT list, or <c>*</c> when there is nothing to project.
    /// </summary>
    /// <param name="reference">
    /// How a source column is written *in this statement*. A reader selecting straight from the table
    /// passes <c>dialect.QuoteIdentifier</c>; the Change Tracking reader, whose statement joins the
    /// table under an alias, passes something that produces <c>base.[Region]</c>. Getting this wrong
    /// is how a transform on a primary key column becomes an ambiguous-column error.
    /// </param>
    public static string Render(
        SqlDialect dialect, IReadOnlyList<ColumnMapping> columnMappings, Func<string, string>? reference = null)
    {
        // No mappings means no projection was specified, which is not the same as "project nothing".
        // A reload triggered before any mapping exists, and every driver test that reads directly,
        // both land here and both want the whole row.
        if (columnMappings.Count == 0)
            return "*";

        reference ??= dialect.QuoteIdentifier;

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var entries = new List<string>(columnMappings.Count);

        foreach (var mapping in columnMappings)
        {
            // Two target columns fed from one source column is legitimate — the same value written
            // twice — but selecting it twice is not, and would give ChangeSchema a duplicate name.
            if (!seen.Add(mapping.SourceColumn))
                continue;

            entries.Add(RenderColumn(dialect, mapping, reference));
        }

        return string.Join(", ", entries);
    }

    private static string RenderColumn(SqlDialect dialect, ColumnMapping mapping, Func<string, string> reference)
    {
        var columnRef = reference(mapping.SourceColumn);
        if (string.IsNullOrWhiteSpace(mapping.Transform))
            return columnRef;

        // An expression with no token is used verbatim, which keeps a literal, another column, or a
        // correlated subquery expressible — a transform is not required to be *about* its own column.
        var expression = mapping.Transform.Contains(ColumnToken, StringComparison.Ordinal)
            ? mapping.Transform.Replace(ColumnToken, columnRef, StringComparison.Ordinal)
            : mapping.Transform;

        return $"{expression} AS {dialect.QuoteIdentifier(mapping.SourceColumn)}";
    }
}
