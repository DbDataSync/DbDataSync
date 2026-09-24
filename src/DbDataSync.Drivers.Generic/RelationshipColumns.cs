using DbDataSync.Core.Config;

namespace DbDataSync.Drivers.Generic;

/// <summary>
/// The relationship-sourced <see cref="ColumnMapping"/>s a statement builder actually has to project,
/// for a reader whose schema is not already driven by <see cref="SourceProjection.Render"/> — Change
/// Tracking and CDC both build their own SELECT list around a fixed set of primary-table columns and
/// only *append* relationship columns to it (see phase 188J), rather than composing the whole list from
/// <c>columnMappings</c> the way the batch/watermark readers do.
/// </summary>
public static class RelationshipColumns
{
    /// <summary>
    /// Deduplicated the same way <see cref="SourceProjection.Render"/> dedupes its own SELECT list — two
    /// <see cref="ColumnMapping"/>s naming the identical (<see cref="ColumnMapping.Relationship"/>,
    /// <see cref="ColumnMapping.SourceColumn"/>) pair are the same physical value, projected once — in
    /// mapping order, primary-sourced (<c>Relationship is null</c>) mappings excluded entirely.
    /// </summary>
    public static IReadOnlyList<ColumnMapping> Distinct(IReadOnlyList<ColumnMapping> columnMappings)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<ColumnMapping>();
        foreach (var mapping in columnMappings)
        {
            if (mapping.Relationship is null)
                continue;
            if (!seen.Add(mapping.Relationship + "\u0000" + mapping.SourceColumn))
                continue;
            result.Add(mapping);
        }

        return result;
    }

    /// <summary>
    /// A reader whose schema is every column of the primary table (Change Tracking, CDC) has no dedupe
    /// step of its own to catch a relationship column sharing a name with one already there — unlike the
    /// batch/watermark readers, which only ever select the columns a mapping actually names.
    /// <see cref="ChangeSchema"/> resolves a duplicate name to whichever occurrence was added last, so a
    /// silent collision here would have a relationship column's ordinal quietly steal a primary column's
    /// name (or vice versa) — wrong data, not an error. Thrown instead, naming the collision, the moment
    /// it would happen rather than left to be discovered downstream.
    /// </summary>
    public static void EnsureNoNameCollision(
        IReadOnlyList<string> primaryColumnNames, IReadOnlyList<ColumnMapping> relationshipMappings, string context)
    {
        var existing = new HashSet<string>(primaryColumnNames, StringComparer.OrdinalIgnoreCase);
        foreach (var mapping in relationshipMappings)
        {
            if (existing.Contains(mapping.SourceColumn))
                throw new InvalidOperationException(
                    $"Relationship '{mapping.Relationship}' column '{mapping.SourceColumn}' has the same " +
                    $"name as a column already on {context} — rename the relationship's own foreign " +
                    "column, or give the primary table's column a different name in its mapping, so the " +
                    "two can be told apart.");
        }
    }
}
