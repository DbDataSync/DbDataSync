using System.Data.Common;
using ClrKernel.Core.Secrets;
using DbDataSync.Api.Configuration;
using DbDataSync.Core.Config;
using DbDataSync.Drivers.Abstractions;
using DbDataSync.Libraries;
using DbDataSync.State;

namespace DbDataSync.Api.Services;

/// <summary>
/// What <see cref="DriverConnectionFactory"/> does, named so a caller can depend on the behaviour
/// rather than the concrete class — the seam <c>ProvisioningService</c>'s tests use to hand it a
/// counting fake instead of a real network connection (phase 105 §5's "one connection per endpoint"
/// guarantee is otherwise invisible to a test).
/// </summary>
public interface IConnectionFactory
{
    Task<(DbConnection Connection, IDriver Driver)> OpenAsync(
        string connectionName, CancellationToken cancellationToken);
}

/// <summary>
/// Opens a driver connection for a configured connection name, resolving its credential through
/// SecretStore at connect time. The API process legitimately opens driver connections for work that
/// isn't data movement — schema browsing (<see cref="MetadataService"/>) and computing a bulk load's
/// segment bounds (<see cref="BulkLoadService"/>) — and this is the one place that happens, so
/// credential resolution isn't repeated per caller.
/// </summary>
public sealed class DriverConnectionFactory(
    ConfigRepository configRepository,
    DriverRegistry driverRegistry,
    SecretStore secretStore,
    LibraryRegistry libraryRegistry,
    ApiOptions apiOptions) : IConnectionFactory
{
    /// <summary>
    /// Phase 109h: the built-in engine's driver id maps to the <c>DbDataSync.Libraries</c> id its
    /// assembly resolves through at first touch. Neither <see cref="DbDataSync.Drivers.MsSql.MsSqlDriver"/>
    /// nor <see cref="DbDataSync.Drivers.Postgres.PostgresDriver"/> calls
    /// <see cref="LibraryRegistry.GetFactory"/> — they still <c>new SqlConnection</c>/
    /// <c>new NpgsqlConnection</c> directly, using the typed provider API (<c>SqlBulkCopy</c>,
    /// <c>SqlDbType</c>, <c>NpgsqlDbType</c>) unchanged — but since
    /// <c>DbDataSync.Drivers.MsSql.csproj</c>/<c>.Postgres.csproj</c> now exclude the runtime asset (see
    /// their own comment), that assembly is no longer physically shipped and has to be *loadable* at
    /// first touch through an installed library's armed resolver instead. Reuses
    /// <see cref="MsSqlStateDialect.LibraryId"/>/<see cref="PostgresStateDialect.LibraryId"/> — the same
    /// ids the state store already resolves these exact two packages through — rather than a second
    /// copy of the string.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string> BuiltInDriverLibraryIds = new Dictionary<string, string>
    {
        [DriverIds.MsSql] = MsSqlStateDialect.LibraryId,
        [DriverIds.Postgres] = PostgresStateDialect.LibraryId,
    };

    public async Task<(DbConnection Connection, IDriver Driver)> OpenAsync(
        string connectionName, CancellationToken cancellationToken)
    {
        var config = configRepository.LoadConnection(connectionName);
        var driver = driverRegistry.Get(config.DriverType);

        await EnsureLibraryInstalledAsync(config.DriverType, cancellationToken);

        var credential = config.AuthMode == AuthMode.SqlAuth
            ? secretStore.Resolve(config.CredentialSecretRef!)
            : null;

        var connection = driver.CreateConnection(config, credential);
        try
        {
            await connection.OpenAsync(cancellationToken);
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }

        return (connection, driver);
    }

    /// <summary>
    /// The auto-seed half of phase 109h's item 3: this is "the connection-creation path" the phase
    /// doc's own open question named — resolved, at implementation time, as *opening* a connection
    /// (this method, the one seam every real use of a connection already goes through: the Test button,
    /// schema browsing, bulk load segmenting, and every provisioning check) rather than *saving* one
    /// (<c>ConnectionsController.Upsert</c>). Saving a connection's config never touches the driver's
    /// typed provider types at all — nothing breaks until something actually opens it — and hooking
    /// Upsert instead would have charged a real <c>dotnet publish</c>-backed install to every test that
    /// merely round-trips connection metadata (the large majority of <c>ConnectionsControllerTests</c>
    /// and friends), not only the ones that matter. Hooking here instead means the cost (and the
    /// coverage) lands exactly on what already opens a real MsSql/Postgres connection — which is also
    /// every existing MsSql/Postgres integration test in this solution, so this is exercised, not just
    /// asserted, the moment those tests run against a fresh repo root with no `libraries/` directory.
    /// </summary>
    private async Task EnsureLibraryInstalledAsync(string driverType, CancellationToken cancellationToken)
    {
        if (!BuiltInDriverLibraryIds.TryGetValue(driverType, out var libraryId)
            || libraryRegistry.Installed.ContainsKey(libraryId))
            return;

        var catalogEntry = KnownLibraries.TryGetById(libraryId)!;
        var result = await LibraryInstaller.InstallOrDeferAsync(
            apiOptions.RepoRoot, libraryId, [new PackageRef(catalogEntry.PackageId, catalogEntry.PinnedVersion)],
            catalogEntry.FactoryType, cancellationToken: cancellationToken);

        // Arms this process's resolver immediately — the same "no restart needed" idiom
        // POST /api/libraries already uses — so the CreateConnection/OpenAsync call right after this
        // returns can actually resolve the assembly it needs.
        libraryRegistry.RegisterInstalled(libraryId);

        if (result.Outcome == LibraryInstaller.LibraryInstallOutcome.PendingRestore)
        {
            throw new InvalidOperationException(
                $"'{libraryId}' has no SDK here to restore it, and no in-image catalog cache hit for it either — " +
                "its manifest was written but it is still pending restore. Run `dbdatasync config library sync` " +
                "on a host with the SDK, then retry.");
        }
    }
}
