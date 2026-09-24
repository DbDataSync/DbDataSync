using System.Text;
using DbDataSync.Core.Config;
using DbDataSync.Core.Sql;

namespace DbDataSync.Drivers.Generic;

/// <summary>
/// Renders one <c>LEFT JOIN</c> per relationship <see cref="RelationshipAliases.Assign"/> assigned an
/// alias to — every relationship a mapping actually references, in declaration order, join keys ANDed.
/// Shared by every statement builder that adds a relationship join to an otherwise plain read
/// (<see cref="BatchReloadStatement.BuildRead"/>, <see cref="WatermarkStatement.BuildRead"/>), so the
/// shape of a relationship join — <c>LEFT JOIN</c>, per-relationship alias, ANDed keys — is defined once.
/// </summary>
public static class RelationshipJoins
{
    /// <param name="baseAlias">
    /// What the join condition's own, primary-table side is called — <c>base</c> for a reader that
    /// aliases the primary table itself; a Change Tracking/CDC-style reader that already joins against a
    /// differently-named base rowset (<c>CT</c>, a table-valued function's own alias) passes that name
    /// instead, so a relationship join composes with whatever join already exists rather than assuming
    /// it invented the base alias.
    /// </param>
    public static string Render(
        SqlDialect dialect, IReadOnlyList<RelationshipConfig>? relationships,
        IReadOnlyDictionary<string, string>? relationshipAliases, string baseAlias = "base")
    {
        if (relationships is null || relationshipAliases is null || relationshipAliases.Count == 0)
            return "";

        var joins = new StringBuilder();
        foreach (var relationship in relationships)
        {
            if (!relationshipAliases.TryGetValue(relationship.Name, out var alias))
                continue;

            var condition = string.Join(" AND ", relationship.JoinKeys.Select(key =>
                $"{baseAlias}.{dialect.QuoteIdentifier(key.LocalColumn)} = {alias}.{dialect.QuoteIdentifier(key.ForeignColumn)}"));
            joins.Append($"\nLEFT JOIN {dialect.QualifyTable(relationship.Schema, relationship.Table)} AS {alias} ON {condition}");
        }

        return joins.ToString();
    }
}
