using DbDataSync.Providers;

namespace DbDataSync.Cli;

/// <summary>Known starting <c>driver.yaml</c> shapes for <c>driver install --from &lt;template&gt;</c> —
/// a dialect and type map someone has already worked out for a common engine, so an operator adding it
/// edits rather than writes from a blank file. Unlisted names, and no <c>--from</c> at all, get a
/// minimal shell instead of a guess.</summary>
public static class DriverTemplates
{
    public static string Render(string? template, string id, string displayName, string factoryType, ProviderPackageRef package) =>
        template?.ToLowerInvariant() switch
        {
            "mysql" => MySql(id, displayName, factoryType, package),
            null => Minimal(id, displayName, factoryType, package),
            _ => throw new InvalidOperationException(
                $"No starter template named '{template}'. Known templates: mysql. Omit --from for a minimal shell."),
        };

    /// <summary>The worked example from the plan doc's §*Worked examples*, verbatim in shape —
    /// watermark + batch reload, information_schema, a starter type map covering the common MySQL
    /// column types. Placeholder tokens rather than string interpolation: the template's own YAML uses
    /// <c>{ }</c> flow-mapping syntax throughout, which would otherwise fight an interpolated string's
    /// own brace-escaping.</summary>
    private static string MySql(string id, string displayName, string factoryType, ProviderPackageRef package) =>
        Fill("""
            id: __ID__
            displayName: __DISPLAY_NAME__
            provider:
              factoryType: "__FACTORY_TYPE__"
              packages:
                - { id: __PACKAGE_ID__, version: "__PACKAGE_VERSION__" }
            dialect:
              quoteIdentifier: backtick        # backtick | doubleQuote | bracket
              parameterPrefix: "@"             # "@" -> @p , ":" -> :p , "?" -> positional
              rowLimit: limitOffset            # LIMIT n OFFSET m   (vs. offsetFetch for OFFSET..FETCH)
              catalog: informationSchema       # informationSchema is the only strategy supported today
              supportsChangeDatabase: true     # false -> a mapping naming another database is a config error
              defaultDatabase: ""              # what to connect to before a mapping names one
              # connectionStringKeys:          # uncomment and edit if this provider's key names differ
              #   host: Server
              #   port: Port
              #   database: Database
              #   username: User Id
              #   password: Password
              #   connectTimeout: Connection Timeout

            # Native type name (with its (p,s) args) -> canonical. Anything unlisted -> Unmappable,
            # which provisioning reports as unsupported rather than guessing a rendering.
            typeMap:
              tinyint:        Int8
              smallint:       Int16
              int:            Int32
              bigint:         Int64
              "decimal(p,s)": { kind: Decimal, precision: p, scale: s }
              double:         Double
              "varchar(n)":   { kind: String, length: n, unicode: true }
              text:           { kind: String, max: true }
              datetime:       Timestamp
              date:           Date
              json:           Json
              blob:           { kind: Binary, max: true }

            # Engine-neutral strategies to offer. All already exist in DbDataSync.Drivers.Generic.
            capabilities:
              readers: [Watermark, BatchReload]
              staging: [StagingTable]
              writers: [DeleteInsert]
            """, id, displayName, factoryType, package);

    private static string Minimal(string id, string displayName, string factoryType, ProviderPackageRef package) =>
        Fill("""
            id: __ID__
            displayName: __DISPLAY_NAME__
            provider:
              factoryType: "__FACTORY_TYPE__"
              packages:
                - { id: __PACKAGE_ID__, version: "__PACKAGE_VERSION__" }
            dialect:
              quoteIdentifier: doubleQuote      # backtick | doubleQuote | bracket
              parameterPrefix: "@"              # "@" -> @p , ":" -> :p , "?" -> positional
              rowLimit: offsetFetch             # limitOffset | offsetFetch
              catalog: informationSchema        # the only strategy supported today
              supportsChangeDatabase: true
              defaultDatabase: ""

            # Empty until filled in — every native type is Unmappable, which provisioning reports
            # rather than guesses at. See a --from mysql install for a filled-in example.
            typeMap: {}

            capabilities:
              readers: [Watermark]
              staging: [StagingTable]
              writers: [DeleteInsert]
            """, id, displayName, factoryType, package);

    private static string Fill(string template, string id, string displayName, string factoryType, ProviderPackageRef package) =>
        template
            .Replace("__ID__", id)
            .Replace("__DISPLAY_NAME__", displayName)
            .Replace("__FACTORY_TYPE__", factoryType)
            .Replace("__PACKAGE_ID__", package.Id)
            .Replace("__PACKAGE_VERSION__", package.Version);
}
