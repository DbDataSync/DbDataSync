using DataSync.Api.Services;
using DataSync.Drivers.Abstractions;

namespace DataSync.Api.Tests;

/// <summary>
/// A catalog that answers from a dictionary and remembers being asked.
/// <para>
/// The same shape, and for the same reason, as <see cref="FakeChangeCounterSource"/>: it fakes the
/// one part of the metadata cache that talks to a database, and everything else the tests exercise —
/// config, endpoint inheritance, the git commit a save makes — is the real thing the API is wired
/// with. Recording the calls matters here too, because "the cache was populated without a second
/// live query" is a claim about how many times this was called, and is invisible in the result.
/// </para>
/// </summary>
public sealed class FakeColumnCatalog : IColumnCatalog
{
    public Dictionary<(string Connection, string Database, string Schema, string Table), List<ColumnMetadata>>
        Tables { get; } = [];

    /// <summary>Tables named here throw the way <c>MetadataService</c> does for a table that is not
    /// in the catalog — the normal state of a target provisioning has yet to create.</summary>
    public HashSet<(string Connection, string Database, string Schema, string Table)> Missing { get; } = [];

    public List<(string Connection, string Database, string Schema, string Table)> Calls { get; } = [];

    public void Set(string connection, string database, string schema, string table,
        params ColumnMetadata[] columns) =>
        Tables[(connection, database, schema, table)] = [.. columns];

    public Task<IReadOnlyList<ColumnMetadata>> ListColumnsAsync(
        string connectionName, string database, string schema, string table, CancellationToken cancellationToken)
    {
        var key = (connectionName, database, schema, table);
        Calls.Add(key);

        if (Missing.Contains(key))
            throw new InvalidOperationException($"Table '{schema}.{table}' was not found in '{database}'.");

        return Task.FromResult<IReadOnlyList<ColumnMetadata>>(
            Tables.TryGetValue(key, out var columns) ? columns : []);
    }
}
