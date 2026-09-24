using DbDataSync.Core.Config;
using DbDataSync.Core.Sql;

namespace DbDataSync.Drivers.Generic;

/// <summary>
/// Assigns each <see cref="RelationshipConfig"/> actually referenced by at least one
/// <see cref="ColumnMapping"/> a stable join alias (<c>r0</c>, <c>r1</c>, ...), in the order the
/// relationships are declared. A relationship nothing maps from is left out entirely — it renders no
/// <c>JOIN</c> at all, per phase 187J.
/// <para>
/// Computed once per statement and handed to both <see cref="SourceProjection.Render"/> (the
/// <c>SELECT</c> list) and <see cref="BatchReloadStatement.BuildRead"/> (the <c>JOIN</c> clauses), so
/// the two agree on which alias names which relationship — the one thing that must never drift between
/// them.
/// </para>
/// </summary>
public static class RelationshipAliases
{
    public static IReadOnlyDictionary<string, string> Assign(
        IReadOnlyList<RelationshipConfig> relationships, IReadOnlyList<ColumnMapping> columnMappings)
    {
        var referenced = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var mapping in columnMappings)
            if (mapping.Relationship is not null)
                referenced.Add(mapping.Relationship);

        var aliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var relationship in relationships)
        {
            if (!referenced.Contains(relationship.Name))
                continue;

            aliases[relationship.Name] = $"r{aliases.Count}";
        }

        return aliases;
    }

    /// <summary>
    /// How a *primary-table* (or, for the Change Tracking/CDC readers, an already-aliased base rowset's)
    /// column is written once at least one relationship is actually joined: qualified through
    /// <paramref name="baseAlias"/>, because an unqualified reference becomes ambiguous the moment a
    /// joined table happens to share that column's name — a foreign lookup table's own "Id" colliding
    /// with the primary table's being the ordinary case, not a contrived one (found for real in 187J's
    /// own integration tests). <c>null</c> — meaning "render exactly as before" — when nothing is
    /// actually joined, so every mapping without a relationship is unaffected.
    /// </summary>
    /// <param name="sourceIsQuery">
    /// Whether the primary source is a hand-written query wrapped as a derived table (phase 191S) rather
    /// than a real table — a wrapped source is aliased <c>base</c> the same as a joined one, even with
    /// zero relationships present, since the wrap itself is what the alias refers to.
    /// </param>
    public static Func<string, string>? PrimaryReference(
        SqlDialect dialect, IReadOnlyDictionary<string, string> relationshipAliases, bool sourceIsQuery = false,
        string baseAlias = "base") =>
        relationshipAliases.Count == 0 && !sourceIsQuery
            ? null
            : column => $"{baseAlias}.{dialect.QuoteIdentifier(column)}";
}
