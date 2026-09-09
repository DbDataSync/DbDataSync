namespace DbDataSync.Libraries;

/// <param name="Id">A short, stable catalog id an operator can type instead of spelling out the
/// package id and factory type by hand — <c>mysql-connector</c>, not
/// <c>MySqlConnector.MySqlConnectorFactory, MySqlConnector</c>. Not necessarily the same string as
/// <paramref name="PackageId"/> (it rarely is, once a package id has dots or unusual casing).</param>
/// <param name="PackageId">The real NuGet package id this entry resolves to — what actually gets
/// restored.</param>
/// <param name="FactoryType">The <see cref="System.Data.Common.DbProviderFactory"/> type this
/// package registers, assembly-qualified.</param>
/// <param name="DisplayName">A human-readable label for a picker — "MySQL / MariaDB (MySqlConnector)",
/// not the bare package id.</param>
/// <param name="Description">One line: what this is, for the same picker.</param>
/// <param name="Vetted">Always <c>true</c> for a bundled entry — the field exists so a caller (the web
/// UI, phase 118) can render "vetted" vs. "you found this" without special-casing every bundled
/// entry as a separate case.</param>
public sealed record LibraryCatalogEntry(
    string Id, string PackageId, string FactoryType, string DisplayName, string Description, bool Vetted = true);

/// <summary>
/// The curated, bundled set of libraries DbDataSync knows how to identify by a short id or a package
/// id — a starter guess for a package's <see cref="System.Data.Common.DbProviderFactory"/> type, and
/// (from phase 117 on) a catalog id shorthand for <c>config library install</c>. Editable after
/// install — <c>library.json</c> is a plain file — so a wrong or unlisted guess is a one-line fix,
/// never a blocker.
/// </summary>
public static class KnownLibraries
{
    public static readonly IReadOnlyList<LibraryCatalogEntry> All =
    [
        new(
            "mysql-connector", "MySqlConnector", "MySqlConnector.MySqlConnectorFactory, MySqlConnector",
            "MySQL / MariaDB (MySqlConnector)", "The MySqlConnector ADO.NET provider for MySQL and MariaDB."),
        new(
            "microsoft-data-sqlclient", "Microsoft.Data.SqlClient", "Microsoft.Data.SqlClient.SqlClientFactory, Microsoft.Data.SqlClient",
            "SQL Server (Microsoft.Data.SqlClient)", "Microsoft's own ADO.NET provider for SQL Server and Azure SQL."),
        new(
            "npgsql", "Npgsql", "Npgsql.NpgsqlFactory, Npgsql",
            "PostgreSQL (Npgsql)", "The Npgsql ADO.NET provider for PostgreSQL."),
        new(
            "microsoft-data-sqlite", "Microsoft.Data.Sqlite", "Microsoft.Data.Sqlite.SqliteFactory, Microsoft.Data.Sqlite",
            "SQLite (Microsoft.Data.Sqlite)", "Microsoft's ADO.NET provider for SQLite."),
        new(
            "oracle-managed-data-access", "Oracle.ManagedDataAccess.Core", "Oracle.ManagedDataAccess.Client.OracleClientFactory, Oracle.ManagedDataAccess",
            "Oracle (Oracle.ManagedDataAccess.Core)", "Oracle's own managed ADO.NET provider."),
        new(
            "system-data-odbc", "System.Data.Odbc", "System.Data.Odbc.OdbcFactory, System.Data.Odbc",
            "ODBC (System.Data.Odbc)", "Generic access to any engine through an installed ODBC driver."),
        new(
            "firebird-client", "FirebirdSql.Data.FirebirdClient", "FirebirdSql.Data.FirebirdClient.FirebirdClientFactory, FirebirdSql.Data.FirebirdClient",
            "Firebird (FirebirdSql.Data.FirebirdClient)", "The community ADO.NET provider for Firebird."),
    ];

    /// <summary>Null when the package isn't in the starter table — the caller (the CLI) then requires
    /// <c>--factory-type</c> explicitly rather than guessing wrong.</summary>
    public static string? TryGet(string packageId) =>
        All.FirstOrDefault(e => string.Equals(e.PackageId, packageId, StringComparison.OrdinalIgnoreCase))?.FactoryType;

    /// <summary>Null when <paramref name="id"/> isn't a catalog id — the caller then treats the value
    /// as a literal package id instead (the pre-117 behaviour, still supported).</summary>
    public static LibraryCatalogEntry? TryGetById(string id) =>
        All.FirstOrDefault(e => string.Equals(e.Id, id, StringComparison.OrdinalIgnoreCase));
}
