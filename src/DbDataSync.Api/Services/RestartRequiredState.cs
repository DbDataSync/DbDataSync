using DbDataSync.Api.Configuration;

namespace DbDataSync.Api.Services;

/// <summary>
/// Whether something changed that this running process won't pick up until it restarts — a config
/// value (phase 81), or (phase 120) a library or driver installed/removed through the web console,
/// since both load once at composition-root startup. A file touch in the repo root rather than
/// in-memory state, so a *different* admin's already-open tab (or the same admin's tab after a reload)
/// still learns about a change another request made, not just the one that made it — the gap this
/// phase's own plan flagged as an open question. Cleared once, at the next real startup.
/// </summary>
public sealed class RestartRequiredState(ApiOptions apiOptions)
{
    private const string MarkerFileName = ".dbdatasync-restart-required";

    private string MarkerPath => Path.Combine(apiOptions.RepoRoot, MarkerFileName);

    public void Touch()
    {
        // Content is only for a human poking around the repo root — nothing reads it back.
        File.WriteAllText(MarkerPath, $"Set {DateTimeOffset.UtcNow:O}");
    }

    public bool IsSet() => File.Exists(MarkerPath);

    /// <summary>Called once, at host startup — a fresh process has, by definition, already picked up
    /// whatever this marker was recording.</summary>
    public void Clear()
    {
        try { File.Delete(MarkerPath); } catch (IOException) { /* best effort */ }
    }
}
