using DbDataSync.Drivers.Abstractions;
using DbDataSync.Drivers.DuckDb;
using DbDataSync.Drivers.MsSql;
using DbDataSync.Drivers.MySql;
using DbDataSync.Drivers.Postgres;

namespace DbDataSync.Cli;

/// <summary>
/// The built-in, compiled replication drivers — the ones with a real
/// <see cref="IDriver.RequiredLibraryId"/> for phase 109j's compatibility checking to apply to. A
/// descriptor or compiled-plugin driver (109d/109e) is never relevant here: its provider is resolved by
/// name through <see cref="System.Data.Common.DbProviderFactories"/> rather than through a compiled
/// driver's own typed member references, so <see cref="IDriver.RequiredLibraryId"/> stays null for it.
/// <para>
/// Its own tiny list rather than reused from <c>DbDataSyncHost.Build</c>/<c>TaskRunner</c>'s
/// <c>Program.cs</c>: those two composition roots register readers/writers/scripting against a live,
/// shared <c>DriverRegistry</c> and need a <c>ScriptHost</c> to do it: <c>library install</c>/<c>sync</c>
/// (<see cref="LibraryCommand"/>) and <c>config check</c> (<see cref="ReadinessChecks"/>) ask a narrower
/// question — "which built-in drivers exist, and what's their <see cref="IDriver.RequiredLibraryId"/>"
/// — with no scripting host, no readers, and no registry needed to answer it.
/// </para>
/// </summary>
internal static class BuiltInDrivers
{
    public static readonly IReadOnlyList<IDriver> All =
    [
        new MsSqlDriver(),
        new PostgresDriver(),
        new MySqlDriver(),
        new DuckDbDriver(),
    ];
}
