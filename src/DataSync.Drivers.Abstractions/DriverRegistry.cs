using DataSync.Core.Config;

namespace DataSync.Drivers.Abstractions;

/// <summary>
/// Enumerates registered drivers so config validation and the UI's task builder can only offer
/// reader/cache/writer combinations a driver actually supports (architecture/detailed-design.md §3.4).
/// </summary>
public sealed class DriverRegistry
{
    private readonly Dictionary<ConnectionDriverType, IDriver> _drivers = new();

    public void Register(IDriver driver) => _drivers[driver.DriverType] = driver;

    public IDriver Get(ConnectionDriverType driverType) =>
        _drivers.TryGetValue(driverType, out var driver)
            ? driver
            : throw new InvalidOperationException($"No driver registered for '{driverType}'.");

    public bool TryGet(ConnectionDriverType driverType, out IDriver? driver) =>
        _drivers.TryGetValue(driverType, out driver);

    public bool SupportsReader(ConnectionDriverType driverType, string kind) =>
        TryGet(driverType, out var driver) && driver!.Readers.Any(r => r.Kind == kind);

    public bool SupportsStagingProvider(ConnectionDriverType driverType, string kind) =>
        TryGet(driverType, out var driver) && driver!.StagingProviders.Any(p => p.Kind == kind);

    public bool SupportsWriter(ConnectionDriverType driverType, string kind) =>
        TryGet(driverType, out var driver) && driver!.Writers.Any(w => w.Kind == kind);
}
