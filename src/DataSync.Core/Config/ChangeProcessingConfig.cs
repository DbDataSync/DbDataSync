namespace DataSync.Core.Config;

/// <summary>
/// A reader/cache/writer "Kind" is a driver-advertised identifier (e.g. "MsSqlChangeTracking",
/// "MsSqlStagingTable", "MsSqlMerge") resolved against the driver registry introduced in Phase 3.
/// Phase 1 does not validate Kind against real drivers yet — only that config shape is well-formed.
/// </summary>
public sealed class ReaderConfig
{
    public required string Kind { get; set; }
    public int Parallelism { get; set; } = 1;
    public Dictionary<string, string> Options { get; set; } = new();
}

public sealed class CacheConfig
{
    public required string Kind { get; set; }
    public Dictionary<string, string> Options { get; set; } = new();
}

public sealed class WriterConfig
{
    public required string Kind { get; set; }
    public int Parallelism { get; set; } = 1;
    public Dictionary<string, string> Options { get; set; } = new();
}

public sealed class ChangeProcessingConfig
{
    public required ReaderConfig Reader { get; set; }
    public required CacheConfig Cache { get; set; }
    public required WriterConfig Writer { get; set; }
}
