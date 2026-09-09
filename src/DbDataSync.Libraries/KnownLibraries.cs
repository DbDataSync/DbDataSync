namespace DbDataSync.Libraries;

/// <summary>
/// A starter guess for a package's <see cref="System.Data.Common.DbProviderFactory"/> type, keyed by
/// the primary NuGet package id an operator would type. Editable after install — <c>library.json</c>
/// is a plain file — so a wrong or unlisted guess is a one-line fix, never a blocker.
/// </summary>
public static class KnownLibraries
{
    private static readonly Dictionary<string, string> ById = new(StringComparer.OrdinalIgnoreCase)
    {
        ["MySqlConnector"] = "MySqlConnector.MySqlConnectorFactory, MySqlConnector",
        ["Microsoft.Data.SqlClient"] = "Microsoft.Data.SqlClient.SqlClientFactory, Microsoft.Data.SqlClient",
        ["Npgsql"] = "Npgsql.NpgsqlFactory, Npgsql",
        ["Microsoft.Data.Sqlite"] = "Microsoft.Data.Sqlite.SqliteFactory, Microsoft.Data.Sqlite",
        ["Oracle.ManagedDataAccess.Core"] = "Oracle.ManagedDataAccess.Client.OracleClientFactory, Oracle.ManagedDataAccess",
        ["System.Data.Odbc"] = "System.Data.Odbc.OdbcFactory, System.Data.Odbc",
        ["FirebirdSql.Data.FirebirdClient"] = "FirebirdSql.Data.FirebirdClient.FirebirdClientFactory, FirebirdSql.Data.FirebirdClient",
    };

    /// <summary>Null when the package isn't in the starter table — the caller (the CLI) then requires
    /// <c>--factory-type</c> explicitly rather than guessing wrong.</summary>
    public static string? TryGet(string packageId) => ById.GetValueOrDefault(packageId);
}
