using DataSync.Core.Config;

namespace DataSync.Drivers.Abstractions;

/// <summary>
/// Enumerates registered drivers so config validation and the UI's task builder can only offer
/// reader/cache/writer combinations a driver actually supports (architecture/detailed-design.md §3.4).
/// </summary>
public sealed class DriverRegistry
{
    private readonly Dictionary<ConnectionDriverType, IDriver> _drivers = new();
    private readonly Dictionary<ConnectionDriverType, IReadOnlyList<IChangeReader>> _hostReaders = new();

    /// <param name="hostReaders">
    /// Readers the *host* supplies for this driver rather than the driver supplying itself — phase 30's
    /// <c>ScriptedQuery</c>, which needs the script host and so cannot be constructed inside a driver
    /// project without dragging Roslyn in with it. Composed here because this is the composition root's
    /// job; from every caller's point of view they are simply readers this driver has.
    /// </param>
    public void Register(IDriver driver, IReadOnlyList<IChangeReader>? hostReaders = null)
    {
        _drivers[driver.DriverType] = driver;
        if (hostReaders is { Count: > 0 })
            _hostReaders[driver.DriverType] = hostReaders;
    }

    /// <summary>Every reader available for this engine — the driver's own plus any the host supplied.
    /// The one place to ask, so a host-supplied reader is not visible to the pipeline but invisible to
    /// the capability endpoint, or the other way round.</summary>
    public IReadOnlyList<IChangeReader> Readers(ConnectionDriverType driverType) =>
        TryGet(driverType, out var driver)
            ? [.. driver!.Readers, .. _hostReaders.TryGetValue(driverType, out var extra) ? extra : []]
            : [];

    public IChangeReader? FindReader(ConnectionDriverType driverType, string kind) =>
        Readers(driverType).FirstOrDefault(r => r.Kind == kind);

    public IDriver Get(ConnectionDriverType driverType) =>
        _drivers.TryGetValue(driverType, out var driver)
            ? driver
            : throw new InvalidOperationException($"No driver registered for '{driverType}'.");

    public bool TryGet(ConnectionDriverType driverType, out IDriver? driver) =>
        _drivers.TryGetValue(driverType, out driver);

    public bool SupportsReader(ConnectionDriverType driverType, string kind) =>
        FindReader(driverType, kind) is not null;

    public bool SupportsStagingProvider(ConnectionDriverType driverType, string kind) =>
        TryGet(driverType, out var driver) && driver!.StagingProviders.Any(p => p.Kind == kind);

    public bool SupportsWriter(ConnectionDriverType driverType, string kind) =>
        TryGet(driverType, out var driver) && driver!.Writers.Any(w => w.Kind == kind);

    /// <summary>Whether the named reader can expand an <see cref="AutoSegment"/> into concrete ranges
    /// — an interface check, so a reader gains the capability by implementing it, not by being added
    /// to a list here.</summary>
    public bool SupportsSegmentation(ConnectionDriverType driverType, string readerKind) =>
        FindReader(driverType, readerKind) is ISegmentExpandingReader;

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
                Readers(driverType)
                    .Select(r => new ReaderCapability(r.Kind, r is ISegmentExpandingReader, r.DetectsDeletes, r.Parameters))
                    .ToList(),
                driver.StagingProviders.Select(p => new StagingCapability(p.Kind, p.Parameters)).ToList(),
                driver.Writers.Select(w => new WriterCapability(w.Kind, w.SupportsReconciliation, w.Parameters)).ToList(),
                driver is IConnectionTester,
                driver is IProvisioner provisioner ? provisioner.SupportedActions : [],
                driver.ConnectionParameters)
            : null;
}
