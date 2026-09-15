using DbDataSync.Core.Config;
using DbDataSync.Libraries;

namespace DbDataSync.State;

/// <summary>
/// Phase 109h: neither <c>DbDataSync.Drivers.MsSql.MsSqlDriver</c> nor
/// <c>DbDataSync.Drivers.Postgres.PostgresDriver</c> calls <see cref="LibraryRegistry.GetFactory"/> —
/// they still <c>new SqlConnection</c>/<c>new NpgsqlConnection</c> directly — but since their csproj
/// files exclude the runtime asset (see those files' own comments), the assembly is no longer physically
/// shipped and has to be *loadable* at first touch through an installed library's armed resolver
/// instead.
/// <para>
/// **Phase 144**: this used to live only in <c>DbDataSync.Api</c>'s <c>DriverConnectionFactory</c> — the
/// API opens a driver connection for schema browsing, provisioning, and bulk-load segmenting, and that
/// was the one call site that ever auto-seeded a built-in driver's library. <c>DbDataSync.TaskRunner</c>
/// is a genuinely separate process, spawned fresh per replication by <c>ProcessSupervisor</c>, and had no
/// equivalent — it depended entirely on the API process having already opened a connection for a given
/// driver type before this process started, with nothing guaranteeing that ordering. When it wasn't true
/// (nothing in the replication's own history had ever opened a connection through the API for that exact
/// driver type before this exact worker process's own first touch), the runner's own
/// <c>SqlConnection</c>/<c>NpgsqlConnection</c> construction threw a bare
/// <c>FileNotFoundException</c> — "Could not load file or assembly ... The system cannot find the file
/// specified" — surfacing to an operator as an undifferentiated Config error with no indication a driver
/// library was ever the cause. Shared here so both processes run the identical check-install-register
/// sequence rather than one silently assuming the other went first.
/// </para>
/// </summary>
public static class BuiltInDriverLibraries
{
    private static readonly IReadOnlyDictionary<string, string> LibraryIds = new Dictionary<string, string>
    {
        [DriverIds.MsSql] = MsSqlStateDialect.LibraryId,
        [DriverIds.Postgres] = PostgresStateDialect.LibraryId,
    };

    /// <summary>
    /// One lock for every library, not one per id — mirrors <c>DriverConnectionFactory</c>'s own
    /// original reasoning: this only ever contends during the narrow, one-time install window for a
    /// given library, and the <c>Installed.ContainsKey</c> fast path below returns before ever touching
    /// it once a library is installed.
    /// <para>
    /// **Known residual gap, not closed here**: this lock is per-process. Two different processes (the
    /// API and a TaskRunner worker, or two TaskRunner workers for two different replications) racing to
    /// install the *same* not-yet-installed library for the first time at the same moment are not
    /// serialized against each other — only ever a narrow window (this application's own usage pattern
    /// has the API touch a driver, via schema browsing, well before any replication using it can even be
    /// configured to run), and a genuine cross-process file lock to close it fully is more than this fix
    /// warrants on its own.
    /// </para>
    /// </summary>
    private static readonly SemaphoreSlim InstallLock = new(1, 1);

    /// <summary>
    /// A no-op for a driver with no required library (DuckDB is embedded, and installed through its own
    /// explicit <c>config library install</c> flow rather than this auto-seed path — see
    /// <c>IDriver.RequiredLibraryId</c>), or one already installed and armed in this process's own
    /// <paramref name="libraryRegistry"/>.
    /// </summary>
    public static async Task EnsureInstalledAsync(
        LibraryRegistry libraryRegistry, string repoRoot, string driverType, CancellationToken cancellationToken)
    {
        if (!LibraryIds.TryGetValue(driverType, out var libraryId)
            || libraryRegistry.Installed.ContainsKey(libraryId))
            return;

        await InstallLock.WaitAsync(cancellationToken);
        try
        {
            // Re-checked inside the lock: a concurrent caller in this same process may have finished
            // installing this exact library while this one was waiting its turn.
            if (libraryRegistry.Installed.ContainsKey(libraryId))
                return;

            var catalogEntry = KnownLibraries.TryGetById(libraryId)!;
            var result = await LibraryInstaller.InstallOrDeferAsync(
                repoRoot, libraryId, [new PackageRef(catalogEntry.PackageId, catalogEntry.PinnedVersion)],
                catalogEntry.FactoryType, cancellationToken: cancellationToken);

            // Arms this process's resolver immediately — the same "no restart needed" idiom
            // POST /api/libraries already uses — so the connection-open call right after this returns
            // can actually resolve the assembly it needs.
            libraryRegistry.RegisterInstalled(libraryId);

            if (result.Outcome == LibraryInstaller.LibraryInstallOutcome.PendingRestore)
            {
                throw new InvalidOperationException(
                    $"'{libraryId}' has no SDK here to restore it, and no in-image catalog cache hit for it either — " +
                    "its manifest was written but it is still pending restore. Run `dbdatasync config library sync` " +
                    "on a host with the SDK, then retry.");
            }
        }
        finally
        {
            InstallLock.Release();
        }
    }
}
