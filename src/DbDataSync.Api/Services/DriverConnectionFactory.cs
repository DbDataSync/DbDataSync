using System.Data.Common;
using ClrKernel.Core.Secrets;
using DbDataSync.Core.Config;
using DbDataSync.Drivers.Abstractions;

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
/// isn't data movement — schema browsing (<see cref="MetadataService"/>) and computing a backfill's
/// segment bounds (<see cref="BackfillService"/>) — and this is the one place that happens, so
/// credential resolution isn't repeated per caller.
/// </summary>
public sealed class DriverConnectionFactory(
    ConfigRepository configRepository,
    DriverRegistry driverRegistry,
    SecretStore secretStore) : IConnectionFactory
{
    public async Task<(DbConnection Connection, IDriver Driver)> OpenAsync(
        string connectionName, CancellationToken cancellationToken)
    {
        var config = configRepository.LoadConnection(connectionName);
        var driver = driverRegistry.Get(config.DriverType);
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
}
