namespace DataSync.State;

/// <summary>
/// Which database backs the state store — see phase 63.
/// <para>
/// Chosen once when a deployment is stood up, not switchable at runtime: the schema is created on
/// first open and there is deliberately no cross-engine migration, so changing this against an
/// existing store starts an empty one rather than moving anything.
/// </para>
/// </summary>
public enum StateEngine
{
    /// <summary>The default, and what an unconfigured deployment gets. A file, no server to run.</summary>
    Sqlite,

    MsSql,

    Postgres,
}
