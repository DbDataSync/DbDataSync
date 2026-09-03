using DbDataSync.Drivers.Abstractions;
using DbDataSync.Drivers.Generic;

namespace DbDataSync.Scripting;

/// <summary>
/// Composes <see cref="ScriptedQueryReader"/> for a driver and registers it alongside the driver's own.
/// <para>
/// It lives here, at the composition root's disposal, rather than inside a driver project, because the
/// reader needs the script host and a driver must not depend on Roslyn. From every caller's point of
/// view — the pipeline, the capability endpoint, the SPA's reader picker — it is simply a reader that
/// driver has.
/// </para>
/// </summary>
public static class ScriptedQueryRegistration
{
    /// <summary>
    /// Registers <paramref name="driver"/> with a <c>ScriptedQuery</c> reader when it names a dialect
    /// and supplies a catalog. A driver that does neither is registered unchanged — it simply does not
    /// offer the Kind, which is the honest answer rather than one that fails at the first run.
    /// </summary>
    public static void RegisterWithScripting(this DriverRegistry registry, IDriver driver, ScriptHost scriptHost)
    {
        var dialect = (driver as IDialectProvider)?.Dialect;
        var catalog = (driver as ITableCatalogProvider)?.Catalog;
        if (dialect is null || catalog is null)
        {
            registry.Register(driver);
            return;
        }

        registry.Register(driver, [new ScriptedQueryReader(scriptHost, dialect, driver.DriverType.ToString(), catalog)]);
    }
}
