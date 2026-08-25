namespace DataSync.Core.Git;

/// <summary>
/// Identity a config-write commit is attributed to. Supplied by the caller on every write rather
/// than fixed inside DataSync.Core, since who the "acting user" is depends on auth (an open question
/// in architecture/detailed-design.md §8, not yet resolved) — callers without real per-user auth yet
/// (e.g. TaskRunner, or the API before Phase 5's auth lands) can pass a fixed system identity.
/// </summary>
public sealed record GitAuthor(string Name, string Email);
