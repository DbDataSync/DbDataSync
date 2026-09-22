namespace DbDataSync.Core.Config;

/// <summary>
/// Merges a replication's endpoints with a table mapping's own settings into the fully-resolved
/// <see cref="TableRef"/> that drivers consume.
/// <para>
/// The whole point of doing this in one place is that no reader, writer or staging provider has to
/// know inheritance exists. They receive a reference with every field filled in, exactly as they did
/// before endpoints were a concept.
/// </para>
/// </summary>
public static class EndpointResolution
{
    public static SourceTableRef ResolveSource(ReplicationTaskConfig task, SourceTableSpec spec) =>
        new()
        {
            ConnectionName = Resolve(spec.ConnectionName, task.Endpoints.Source?.ConnectionName, task.Name, "source", "connection"),
            Database = Resolve(spec.Database, task.Endpoints.Source?.Database, task.Name, "source", "database"),
            Schema = spec.Schema,
            Table = spec.Table,
            Filter = spec.Filter,
        };

    public static TableRef ResolveTarget(ReplicationTaskConfig task, TableSpec spec) =>
        new()
        {
            ConnectionName = Resolve(spec.ConnectionName, task.Endpoints.Target?.ConnectionName, task.Name, "target", "connection"),
            Database = Resolve(spec.Database, task.Endpoints.Target?.Database, task.Name, "target", "database"),
            Schema = spec.Schema,
            Table = spec.Table,
        };

    /// <summary>
    /// Validates that every side of a mapping resolves, and throws naming the side and the field that
    /// doesn't. Called at save time so a mapping that cannot run is rejected while the operator is
    /// still looking at it, rather than at the first run.
    /// </summary>
    public static void Validate(ReplicationTaskConfig task, TableMappingConfig mapping)
    {
        foreach (var source in mapping.Sources)
            ResolveSource(task, source);
        foreach (var target in mapping.Targets)
            ResolveTarget(task, target);
    }

    /// <summary>
    /// Each field falls back independently, so a mapping can override just the database on the
    /// replication's connection without having to restate the connection.
    /// <para>
    /// <c>field == "database"</c> only: an explicit <c>""</c> (not null — null still means "not set
    /// here, check the next level") is a real, deliberate answer, not a missing one — see
    /// <see cref="TableSpec.Database"/>'s own doc comment. Some engines have no separate database to
    /// name (a JDBC connection whose URL already fixes one; Oracle, DuckDB) and this is how an operator
    /// says so on purpose. Checked at the mapping's own level first, so an explicit <c>""</c> there is
    /// never silently overridden by whatever the replication's endpoint says. <c>"connection"</c> never
    /// gets this — a connection name is never optional.
    /// </para>
    /// </summary>
    private static string Resolve(string? own, string? inherited, string taskName, string side, string field)
    {
        if (field == "database")
        {
            if (own == "")
                return "";
            if (string.IsNullOrWhiteSpace(own) && inherited == "")
                return "";
        }

        var value = string.IsNullOrWhiteSpace(own) ? inherited : own;
        if (string.IsNullOrWhiteSpace(value))
            throw new ConfigValidationException(
                $"No {side} {field} for this table mapping: it doesn't set one, and replication " +
                $"'{taskName}' has no {side} endpoint {field} for it to inherit.");
        return value;
    }
}
