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

    /// <summary>Whether the named reader can expand an <see cref="AutoSegment"/> into concrete ranges
    /// — an interface check, so a reader gains the capability by implementing it, not by being added
    /// to a list here.</summary>
    public bool SupportsSegmentation(ConnectionDriverType driverType, string readerKind) =>
        TryGet(driverType, out var driver)
        && driver!.Readers.FirstOrDefault(r => r.Kind == readerKind) is ISegmentExpandingReader;

    /// <summary>Whether the named writer removes target rows absent from the change set within the
    /// scope it was given (see <see cref="IChangeWriter.SupportsReconciliation"/>).</summary>
    public bool SupportsReconciliation(ConnectionDriverType driverType, string writerKind) =>
        TryGet(driverType, out var driver)
        && driver!.Writers.FirstOrDefault(w => w.Kind == writerKind) is { SupportsReconciliation: true };

    /// <summary>Everything a caller needs to offer valid reader/cache/writer choices for this engine,
    /// or null when no driver is registered for it.</summary>
    public DriverCapabilities? Describe(ConnectionDriverType driverType) =>
        TryGet(driverType, out var driver)
            ? new DriverCapabilities(
                driverType,
                driver!.Readers.Select(r => new ReaderCapability(r.Kind, r is ISegmentExpandingReader, r.DetectsDeletes)).ToList(),
                driver.StagingProviders.Select(p => new StagingCapability(p.Kind)).ToList(),
                driver.Writers.Select(w => new WriterCapability(w.Kind, w.SupportsReconciliation)).ToList())
            : null;
}
