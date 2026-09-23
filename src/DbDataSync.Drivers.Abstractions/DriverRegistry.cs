using System.Diagnostics.CodeAnalysis;
using System.Linq;
using DbDataSync.Core.Config;

namespace DbDataSync.Drivers.Abstractions;

/// <summary>
/// Enumerates registered drivers so config validation and the UI's task builder can only offer
/// reader/cache/writer combinations a driver actually supports (architecture/detailed-design.md §3.4).
/// </summary>
public sealed class DriverRegistry
{
    private readonly Dictionary<string, IDriver> _drivers = new(StringComparer.Ordinal);
    private readonly Dictionary<string, IReadOnlyList<IChangeReader>> _hostReaders = new(StringComparer.Ordinal);

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

    /// <summary>Every registered driver — for <c>GET /api/drivers</c> (phase 109d), the one place an
    /// operator sees what's available without already knowing an id to ask <see cref="Get"/> for.</summary>
    public IReadOnlyCollection<IDriver> All => _drivers.Values;

    /// <summary>Every reader available for this engine — the driver's own plus any the host supplied.
    /// The one place to ask, so a host-supplied reader is not visible to the pipeline but invisible to
    /// the capability endpoint, or the other way round.</summary>
    public IReadOnlyList<IChangeReader> Readers(string driverType) =>
        TryGet(driverType, out var driver)
            ? [.. driver!.Readers, .. _hostReaders.TryGetValue(driverType, out var extra) ? extra : []]
            : [];

    public IChangeReader? FindReader(string driverType, string kind) =>
        Readers(driverType).FirstOrDefault(r => r.Kind == kind);

    public IDriver Get(string driverType) =>
        _drivers.TryGetValue(driverType, out var driver)
            ? driver
            : throw new InvalidOperationException($"No driver registered for '{driverType}'.");

    public bool TryGet(string driverType, [NotNullWhen(true)] out IDriver? driver) =>
        _drivers.TryGetValue(driverType, out driver);

    public bool SupportsReader(string driverType, string kind) =>
        FindReader(driverType, kind) is not null;

    public bool SupportsStagingProvider(string driverType, string kind) =>
        TryGet(driverType, out var driver) && driver!.StagingProviders.Any(p => p.Kind == kind);

    public bool SupportsWriter(string driverType, string kind) =>
        TryGet(driverType, out var driver) && driver!.Writers.Any(w => w.Kind == kind);

    /// <summary>Whether the named reader can expand an <see cref="AutoSegment"/> into concrete ranges
    /// — an interface check, so a reader gains the capability by implementing it, not by being added
    /// to a list here.</summary>
    public bool SupportsSegmentation(string driverType, string readerKind) =>
        FindReader(driverType, readerKind) is ISegmentExpandingReader;

    /// <summary>Whether the named writer removes target rows absent from the change set within the
    /// scope it was given (see <see cref="IChangeWriter.SupportsReconciliation"/>).</summary>
    public bool SupportsReconciliation(string driverType, string writerKind) =>
        TryGet(driverType, out var driver)
        && driver!.Writers.FirstOrDefault(w => w.Kind == writerKind) is { SupportsReconciliation: true };

    /// <summary>Everything a caller needs to offer valid reader/cache/writer choices for this engine,
    /// or null when no driver is registered for it.</summary>
    public DriverCapabilities? Describe(string driverType) =>
        TryGet(driverType, out var driver)
            ? new DriverCapabilities(
                driverType,
                Readers(driverType)
                    .Select(r => new ReaderCapability(
                        r.Kind, r is ISegmentExpandingReader, r.DetectsDeletes, r.Parameters,
                        r is IReadIntentDeclaring declaring
                            ? declaring.SupportedIntents.OrderBy(i => i).ToList()
                            : []))
                    .ToList(),
                driver.StagingProviders.Select(p => new StagingCapability(p.Kind, p.Parameters)).ToList(),
                driver.Writers.Select(w => new WriterCapability(w.Kind, w.SupportsReconciliation, w.Parameters)).ToList(),
                driver is IConnectionTester,
                driver is IProvisioner provisioner ? provisioner.SupportedActions : [],
                // Phase 109j: hide "Validate library" the same way SupportsConnectionTest already
                // hides "Test" — a driver with no RequiredLibraryId (descriptor-driven, resolved by
                // name through DbProviderFactories) or no staging provider/writer of its own (DuckDb)
                // has nothing this action could ever exercise.
                driver.RequiredLibraryId is not null && driver.StagingProviders.Count > 0 && driver.Writers.Count > 0,
                driver is IConnectionTester tester ? tester.DefaultTestQuery : null)
            : null;
}
