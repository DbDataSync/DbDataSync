using System.Data.Common;

namespace DbDataSync.Drivers.Jdbc.Imported;

// Ported as-is from ClrKernel.Database.Provider.Jdbc (Apache-2.0, github.com/ClrKernel/ClrKernel,
// src/ClrKernel.Database.Provider.Jdbc/JdbcConnectionStringBuilder.cs) — phase 165V. Reserves
// JdbcDriver/JdbcUrl; every other key becomes a java.util.Properties entry, user/password included —
// matching architecture/planning/todo/jdbc-driver-support.md's connection-model table (credential goes
// in as a property, never appended to the URL).
internal sealed class JdbcConnectionStringBuilder : DbConnectionStringBuilder
{
    public JdbcConnectionStringBuilder() { }
    public JdbcConnectionStringBuilder(string connectionString) { ConnectionString = connectionString; }

    private const string JdbcDriverKey = "JdbcDriver";
    private const string JdbcUrlKey = "JdbcUrl";

    public string? JdbcDriver
    {
        get => ContainsKey(JdbcDriverKey) ? (string)this[JdbcDriverKey] : null;
        set => this[JdbcDriverKey] = value;
    }

    public string? JdbcUrl
    {
        get => ContainsKey(JdbcUrlKey) ? (string)this[JdbcUrlKey] : null;
        set => this[JdbcUrlKey] = value;
    }

    public java.util.Properties GetProperties()
    {
        var result = new java.util.Properties();
        foreach (string key in Keys!)
        {
            if (key == JdbcDriverKey || key == JdbcUrlKey)
                continue;

            switch (this[key])
            {
                case null:
                    break;
                case string value:
                    result.setProperty(key, value);
                    break;
                case { } value:
                    throw new InvalidOperationException(
                        $"JdbcConnectionStringBuilder: values must be strings. Key '{key}' had type '{value.GetType()}'.");
            }
        }
        return result;
    }

    public static string CreateConnectionString(string jdbcDriver, string jdbcUrl, java.util.Properties? properties = null)
    {
        var cs = new JdbcConnectionStringBuilder { JdbcDriver = jdbcDriver, JdbcUrl = jdbcUrl };
        object[] propertyNames = properties?.stringPropertyNames()?.toArray() ?? [];
        foreach (string propertyName in propertyNames)
            cs[propertyName] = properties?.getProperty(propertyName);
        return cs.ConnectionString;
    }
}
