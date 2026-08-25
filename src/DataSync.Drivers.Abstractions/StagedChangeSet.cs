namespace DataSync.Drivers.Abstractions;

/// <summary>Handle to a change set persisted by an <see cref="IStagingProvider"/>, opaque to the
/// caller and passed straight through to an <see cref="IChangeWriter"/>.</summary>
public sealed record StagedChangeSet(string StagingLocation, long RowCount);

public sealed record WriteResult(long RowsWritten);
