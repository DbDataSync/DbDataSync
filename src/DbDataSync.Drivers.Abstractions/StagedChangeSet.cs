namespace DbDataSync.Drivers.Abstractions;

/// <summary>Handle to a change set persisted by an <see cref="IStagingProvider"/>, opaque to the
/// caller and passed straight through to an <see cref="IChangeWriter"/>.</summary>
/// <param name="HasChangeOrdering">Whether the staging provider found and persisted both
/// <see cref="ChangeOrdering"/> columns — phase 132. Defaulted so every existing construction site
/// stays valid; a provider that staged them sets it, and a writer that finds it true can process a key
/// with more than one staged row in true source order instead of colliding.</param>
public sealed record StagedChangeSet(string StagingLocation, long RowCount, bool HasChangeOrdering = false);

public sealed record WriteResult(long RowsWritten);
