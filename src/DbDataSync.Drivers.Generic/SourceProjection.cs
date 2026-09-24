using DbDataSync.Core.Config;
using DbDataSync.Core.Sql;

namespace DbDataSync.Drivers.Generic;

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
    private static readonly IReadOnlyDictionary<string, string> NoRelationships =
        new Dictionary<string, string>();

    /// <summary>
    /// The SELECT list, or <c>*</c> when there is nothing to project.
    /// </summary>
    /// <param name="reference">
    /// How a *primary-table* source column is written *in this statement*. A reader selecting straight
    /// from the table passes <c>dialect.QuoteIdentifier</c>; the Change Tracking reader, whose
    /// statement joins the table under an alias, passes something that produces <c>base.[Region]</c>.
    /// Getting this wrong is how a transform on a primary key column becomes an ambiguous-column error.
    /// </param>
    /// <param name="relationshipAliases">
    /// From <see cref="RelationshipAliases.Assign"/> — omitted (or empty) for a reader that doesn't
    /// support relationships at all, or a mapping that declares none. A <see cref="ColumnMapping"/>
    /// whose <see cref="ColumnMapping.Relationship"/> is set is always rendered against that
    /// relationship's own join alias (e.g. <c>r0.[Region]</c>), never through <paramref name="reference"/>
    /// — that callback is for the primary table only. See phase 187J.
    /// </param>
    public static string Render(
        SqlDialect dialect,
        IReadOnlyList<ColumnMapping> columnMappings,
        Func<string, string>? reference = null,
        IReadOnlyDictionary<string, string>? relationshipAliases = null)
    {
        // No mappings means no projection was specified, which is not the same as "project nothing".
        // A reload triggered before any mapping exists, and every driver test that reads directly,
        // both land here and both want the whole row.
        if (columnMappings.Count == 0)
            return "*";

        reference ??= dialect.QuoteIdentifier;
        relationshipAliases ??= NoRelationships;

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var entries = new List<string>(columnMappings.Count);

        foreach (var mapping in columnMappings)
        {
            // Two target columns fed from one source column is legitimate — the same value written
            // twice — but selecting it twice is not, and would give ChangeSchema a duplicate name.
            // Keyed on (Relationship, SourceColumn) together, not SourceColumn alone — a relationship's
            // foreign table can easily share a column name with the primary table (or with another
            // relationship's foreign table); keying on the bare name would wrongly treat those as the
            // same physical column and silently drop one of them.
            if (!seen.Add((mapping.Relationship ?? "") + "\u0000" + mapping.SourceColumn))
                continue;

            entries.Add(RenderColumn(dialect, mapping, reference, relationshipAliases));
        }

        return SelectListFormatting.JoinSelectList(entries);
    }

    private static string RenderColumn(
        SqlDialect dialect, ColumnMapping mapping, Func<string, string> reference,
        IReadOnlyDictionary<string, string> relationshipAliases)
    {
        var columnReference = ResolveReference(dialect, mapping, reference, relationshipAliases);
        var expression = RenderExpression(mapping, columnReference);
        return string.IsNullOrWhiteSpace(mapping.Transform)
            ? expression
            : $"{expression} AS {dialect.QuoteIdentifier(mapping.SourceColumn)}";
    }

    /// <summary>A relationship-sourced mapping is always read off its own join alias — the primary
    /// table's <paramref name="reference"/> callback has no way to reach a joined table's column, and
    /// isn't asked to.</summary>
    private static Func<string, string> ResolveReference(
        SqlDialect dialect, ColumnMapping mapping, Func<string, string> reference,
        IReadOnlyDictionary<string, string> relationshipAliases)
    {
        if (mapping.Relationship is null)
            return reference;

        if (!relationshipAliases.TryGetValue(mapping.Relationship, out var alias))
            throw new InvalidOperationException(
                $"Column '{mapping.SourceColumn}' names relationship '{mapping.Relationship}', which has " +
                "no assigned join alias — either it isn't declared on this mapping, or the caller built " +
                "relationshipAliases from a different columnMappings list than this one.");

        return column => $"{alias}.{dialect.QuoteIdentifier(column)}";
    }

    /// <summary>
    /// One column's source-side expression, transform applied, **without** an alias — for a caller
    /// that needs the value rather than a select-list entry. A verification check grouping by a
    /// transformed column has to group by what the transform produces, or it compares the source's
    /// raw value against a target holding the transformed one and reports a difference that is not
    /// one.
    /// </summary>
    public static string RenderExpression(ColumnMapping mapping, Func<string, string> reference)
    {
        var columnRef = reference(mapping.SourceColumn);
        if (string.IsNullOrWhiteSpace(mapping.Transform))
            return columnRef;

        // An expression with no token is used verbatim, which keeps a literal, another column, or a
        // correlated subquery expressible — a transform is not required to be *about* its own column.
        return mapping.Transform.Contains(ColumnMapping.ColumnToken, StringComparison.Ordinal)
            ? mapping.Transform.Replace(ColumnMapping.ColumnToken, columnRef, StringComparison.Ordinal)
            : mapping.Transform;
    }
}

