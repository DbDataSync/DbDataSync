using DbDataSync.Core.Config;

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
}
