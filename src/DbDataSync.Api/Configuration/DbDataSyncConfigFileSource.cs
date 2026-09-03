namespace DbDataSync.Api.Configuration;

/// <summary>
/// Wraps <c>dbdatasync.config.yaml</c>'s flattened contents as an <see cref="IConfigurationSource"/> —
/// what <see cref="DbDataSync.Api.DbDataSyncHost.InsertConfigFile"/> inserts into
/// <c>builder.Configuration.Sources</c>.
/// <para>
/// A dedicated type rather than the framework's own <c>MemoryConfigurationSource</c>/
/// <c>MemoryConfigurationProvider</c> (which is what a plain in-memory dictionary would otherwise use)
/// so <c>AdminConfigService</c> (phase 81) can tell "this key's effective value came from the file"
/// apart from any other in-memory configuration source by the provider's actual type — including a
/// test's own <c>ConfigureAppConfiguration(cfg =&gt; cfg.AddInMemoryCollection(...))</c>, which uses the
/// framework type and would otherwise be indistinguishable from the file by shape alone.
/// </para>
/// </summary>
public sealed class DbDataSyncConfigFileSource : IConfigurationSource
{
    public required IEnumerable<KeyValuePair<string, string?>> InitialData { get; init; }

    public IConfigurationProvider Build(IConfigurationBuilder builder) => new DbDataSyncConfigFileProvider(InitialData);
}

public sealed class DbDataSyncConfigFileProvider(IEnumerable<KeyValuePair<string, string?>> initialData)
    : ConfigurationProvider
{
    public override void Load()
    {
        foreach (var (key, value) in initialData)
            Data[key] = value;
    }
}
