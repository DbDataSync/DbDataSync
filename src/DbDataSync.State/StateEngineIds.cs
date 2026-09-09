namespace DbDataSync.State;

/// <summary>
/// The built-in state-store engine ids. <c>ApiOptions.StateEngine</c> (and every dialect's own
/// <see cref="StateDialect.Engine"/>) is a plain string, not a closed enum — phase 109f applies the
/// same move phase 109a made for a replication connection's driver id, so a custom
/// <see cref="StateDialect"/> (a compiled plugin, per <c>architecture/planning/todo/nuget-loaded-drivers.md</c>
/// §*Custom state dialects as an extension*) can be *registered* with <see cref="StateDialectRegistry"/>
/// rather than added to a closed list. These constants exist so the three built-ins stay greppable and
/// a rename is one edit.
/// <para>
/// Chosen once when a deployment is stood up, not switchable at runtime: the schema is created on
/// first open and there is deliberately no cross-engine migration, so changing this against an
/// existing store starts an empty one rather than moving anything.
/// </para>
/// </summary>
public static class StateEngineIds
{
    /// <summary>The default, and what an unconfigured deployment gets. A file, no server to run.</summary>
    public const string Sqlite = "Sqlite";

    public const string MsSql = "MsSql";

    public const string Postgres = "Postgres";
}
