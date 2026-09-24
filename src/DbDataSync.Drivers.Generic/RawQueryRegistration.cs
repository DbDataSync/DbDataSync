using DbDataSync.Core.Config;
using DbDataSync.Drivers.Abstractions;

namespace DbDataSync.Drivers.Generic;

/// <summary>
/// Composes a <see cref="RawQueryReader"/> for a driver and registers it alongside the driver's own —
/// the mechanism that makes "a mapping's source is a query I wrote, not a table" available to any
/// driver, not just DuckDB (where it began as <c>DuckDbQueryReader</c>, a fixed, driver-owned reader —
/// see <see cref="RawQueryReader"/>'s own doc comment).
/// <para>
/// Called for every driver a composition root registers — the five hand-written built-ins, and every
/// descriptor/compiled driver <c>DriverLoader</c> builds — unlike <c>ScriptedQueryRegistration</c>,
/// which today only reaches the hand-written five. A raw query needs nothing a descriptor-driven driver
/// doesn't already have (a dialect; no catalog, no script host), so there's no reason to leave it out
/// of that path the way <c>ScriptedQueryReader</c> currently is.
/// </para>
/// </summary>
public static class RawQueryRegistration
{
    /// <summary>The Kind every driver other than DuckDB offers this reader under. Not "DuckDbQuery" —
    /// that name is DuckDB's own history (existing mappings already have it saved), and offering the
    /// identical capability under a DuckDB-flavoured name on a Postgres or MsSql source would be a
    /// confusing label for what it actually is.</summary>
    public const string Kind = "Query";

    /// <summary>
    /// Registers <paramref name="driver"/> with a raw-query reader when it names a dialect. A driver
    /// that doesn't is registered unchanged — it simply does not offer the Kind, the same "honest
    /// absence rather than a Kind that fails at the first run" posture <c>ScriptedQueryRegistration</c>
    /// already takes.
    /// <para>
    /// DuckDB is skipped deliberately: it already offers this exact capability, under its own
    /// <c>DuckDbQueryReader</c>/<c>"DuckDbQuery"</c> Kind, as one of its own <see cref="IDriver.Readers"/>
    /// — registering the shared reader for it too would put two reader choices with identical behavior
    /// in front of an operator picking one.
    /// </para>
    /// </summary>
    public static void RegisterWithRawQuery(this DriverRegistry registry, IDriver driver)
    {
        if (driver.DriverType == DriverIds.DuckDb || driver is not IDialectProvider dialectProvider)
        {
            registry.Register(driver);
            return;
        }

        registry.Register(driver, [new RawQueryReader(Kind, dialectProvider.Dialect)]);
    }
}
