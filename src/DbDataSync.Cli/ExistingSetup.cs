using DbDataSync.Core.Config;

namespace DbDataSync.Cli;

/// <summary>
/// Whether a repo root already has a real configuration, or is still the fully-commented starter
/// <c>ServeCommand.Prepare</c> writes on a genuinely fresh root — the question <c>setup</c> asks before
/// deciding between the review screen and the walk-through.
/// <para>
/// **Any uncommented key, or a state database on disk.** A commented-out starter file parses to zero
/// live keys (a YAML comment is not a key at all, so <see cref="DbDataSyncConfigFile.Read"/> already
/// returns empty for one) — so "any key at all" is exactly "somebody set something", with no separate
/// parsing of comments needed. The state-database check catches the case a deployment was pointed at
/// entirely by <c>--DbDataSync:*</c> flags or environment variables and never wrote the file at all:
/// <c>serve</c> has still run there for real, so <c>setup</c> should review it, not offer to walk
/// through it as if it were new.
/// </para>
/// </summary>
public static class ExistingSetup
{
    public static bool DetectedAt(string root) =>
        DbDataSyncConfigFile.Read(root).Count > 0 || File.Exists(Path.Combine(root, "state.db"));
}
