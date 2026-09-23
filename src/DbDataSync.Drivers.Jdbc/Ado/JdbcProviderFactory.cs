using System.Data.Common;

namespace DbDataSync.Drivers.Jdbc.Ado;

// Adapted from ClrKernel.Database.Provider.Jdbc (Apache-2.0, github.com/ClrKernel/ClrKernel,
// src/ClrKernel.Database.Provider.Jdbc/JdbcProviderFactory.cs) — phase 165V. Loads a Java JDBC driver
// (from a jar via URLClassLoader, or an IKVM-compiled assembly) and hands out JDBC connections wrapped
// as ADO.NET connections. CreateParameter is new here — the imported original never needed one, since it
// had no parameter support at all (see JdbcCommand).
internal sealed class JdbcProviderFactory : DbProviderFactory
{
    private static readonly Dictionary<string, JdbcProviderFactory> Factories = new();

    public static JdbcProviderFactory? FindByDriver(string driverClass) =>
        Factories.TryGetValue(driverClass, out var factory) ? factory : null;

    public static JdbcProviderFactory FromAssemblyPath(string assemblyPath, string driverClass) =>
        new(() =>
        {
            var assembly = System.Reflection.Assembly.LoadFrom(assemblyPath);
            var driverType = assembly.GetType(driverClass, true);
            return (java.sql.Driver)Activator.CreateInstance(driverType!)!;
        }, driverClass);

    public static JdbcProviderFactory FromJarPath(string jarPath, string driverClass) =>
        FromJarPaths([jarPath], driverClass);

    /// <summary>
    /// Phase 169V. <c>java.net.URLClassLoader</c>'s constructor already takes an array of URLs and treats
    /// them as one combined classpath — a real vendor driver is not always one jar (Oracle's wallet
    /// support needs <c>oraclepki.jar</c>/<c>osdt_cert.jar</c>/<c>osdt_core.jar</c> alongside
    /// <c>ojdbc8.jar</c>; Db2 ships a separate license jar). Class resolution
    /// (<see cref="FromClassLoader"/>'s <c>Class.forName</c>) needs no change either way: it already
    /// searches the whole combined classpath a <c>URLClassLoader</c> was built from, one jar or several.
    /// </summary>
    public static JdbcProviderFactory FromJarPaths(IReadOnlyList<string> jarPaths, string driverClass) =>
        FromClassLoader(
            new java.net.URLClassLoader(
                jarPaths.Select(p => new java.net.URL(new java.io.File(p).toURI().toString())).ToArray()),
            driverClass);

    public static JdbcProviderFactory FromClassLoader(java.lang.ClassLoader classLoader, string driverClass)
    {
        var cls = java.lang.Class.forName(driverClass, true, classLoader);
        return FromDriver((java.sql.Driver)cls.newInstance(), driverClass);
    }

    public static JdbcProviderFactory FromDriver(java.sql.Driver driver, string? driverClass = null) =>
        new(() => driver, driverClass ?? driver.GetType().FullName!);

    private JdbcProviderFactory(Func<java.sql.Driver> loadDriver, string driverClass)
    {
        _driver = new Lazy<java.sql.Driver>(loadDriver);
        _driverClass = driverClass;
        Factories[driverClass] = this;
    }

    private readonly string _driverClass;
    private readonly Lazy<java.sql.Driver> _driver;

    public java.sql.Driver JdbcDriver => _driver.Value;

    public java.sql.Connection GetJdbcConnection(string url, java.util.Properties? properties = null) =>
        JdbcDriver.connect(url, properties ?? new java.util.Properties());

    public override DbConnectionStringBuilder CreateConnectionStringBuilder() =>
        new JdbcConnectionStringBuilder { JdbcDriver = _driverClass };

    public override DbParameter CreateParameter() => new JdbcParameter();

    public override DbConnection CreateConnection() => new JdbcConnection();
}
