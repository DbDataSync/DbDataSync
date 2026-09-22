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
/// <param name="PinnedVersion">The exact version phase 121's <c>internal build-catalog-cache</c>
/// restores into the runtime-only image's in-image cache, and the only version
/// <see cref="LibraryInstaller.InstallOrDeferAsync"/> can serve from that cache with no SDK present.
/// An operator asking for a different version of a curated package on that image still needs an SDK
/// somewhere to satisfy it (<c>config library sync</c>) — the cache is a fast path for the common
/// case, not a second source of truth for "whatever version someone wants."</param>
public sealed record LibraryCatalogEntry(
    string Id, string PackageId, string FactoryType, string DisplayName, string Description,
    string PinnedVersion, bool Vetted = true);

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
            "MySQL / MariaDB (MySqlConnector)", "The MySqlConnector ADO.NET provider for MySQL and MariaDB.",
            PinnedVersion: "2.4.0"),
        new(
            "microsoft-data-sqlclient", "Microsoft.Data.SqlClient", "Microsoft.Data.SqlClient.SqlClientFactory, Microsoft.Data.SqlClient",
            "SQL Server (Microsoft.Data.SqlClient)", "Microsoft's own ADO.NET provider for SQL Server and Azure SQL.",
            PinnedVersion: "7.0.2"),
        new(
            "npgsql", "Npgsql", "Npgsql.NpgsqlFactory, Npgsql",
            "PostgreSQL (Npgsql)", "The Npgsql ADO.NET provider for PostgreSQL.",
            PinnedVersion: "9.0.3"),
        new(
            "microsoft-data-sqlite", "Microsoft.Data.Sqlite", "Microsoft.Data.Sqlite.SqliteFactory, Microsoft.Data.Sqlite",
            "SQLite (Microsoft.Data.Sqlite)", "Microsoft's ADO.NET provider for SQLite.",
            PinnedVersion: "10.0.11"),
        new(
            "oracle-managed-data-access", "Oracle.ManagedDataAccess.Core", "Oracle.ManagedDataAccess.Client.OracleClientFactory, Oracle.ManagedDataAccess",
            "Oracle (Oracle.ManagedDataAccess.Core)", "Oracle's own managed ADO.NET provider.",
            PinnedVersion: "23.9.1"),
        new(
            "system-data-odbc", "System.Data.Odbc", "System.Data.Odbc.OdbcFactory, System.Data.Odbc",
            "ODBC (System.Data.Odbc)", "Generic access to any engine through an installed ODBC driver.",
            PinnedVersion: "10.0.0"),
        new(
            "firebird-client", "FirebirdSql.Data.FirebirdClient", "FirebirdSql.Data.FirebirdClient.FirebirdClientFactory, FirebirdSql.Data.FirebirdClient",
            "Firebird (FirebirdSql.Data.FirebirdClient)", "The community ADO.NET provider for Firebird.",
            PinnedVersion: "10.3.1"),
        // Phase 109i: unlike the seven entries above, nothing installs this "when chosen" — DuckDB
        // backs verification's result paging and every DuckDB-kind segmenting strategy regardless of
        // which replication engines a deployment ever configures, so ServeCommand installs it
        // unconditionally on every `serve` start instead. Factory type confirmed by reflecting the
        // real 1.5.5 assembly rather than guessed: DuckDB.NET.Data.Full's own DbProviderFactory
        // subclass is DuckDBClientFactory (with a public static Instance field DbProviderFactories'
        // string-registration path doesn't need but that's how the package itself exposes it).
        new(
            "duckdb", "DuckDB.NET.Data.Full", "DuckDB.NET.Data.DuckDBClientFactory, DuckDB.NET.Data",
            "DuckDB (embedded)", "The embedded analytical engine DbDataSync's own verification and custom " +
                "segmenting strategies run on.",
            PinnedVersion: "1.5.5"),
        // Phase 165V: genuinely not a DbProviderFactory-bearing package — IKVM is the Java-on-.NET
        // runtime DbDataSync.Drivers.Jdbc uses to run a JDBC driver inside .NET, not an ADO.NET client
        // library. FactoryType here names a real, public type in the assembly DbDataSync.Drivers.Jdbc's
        // own compiled IL actually references (java.sql.Types, in IKVM.Java — confirmed by reflecting
        // the real assemblies rather than guessed, since IKVM.Runtime is a different assembly
        // from the one the java.sql.*/java.util.* surface lives in; re-confirmed against 8.16.1 at
        // phase 170V — IKVM.Java is still the assembly carrying that surface), purely so this entry has an
        // assembly name for DriverLibraryCompatibility's surface check to use (see
        // DriverLibraryCompatibility.AssemblyNameFrom) — it is never registered as a real
        // DbProviderFactory and DbProviderFactories.GetFactory("ikvm") is never called. `dbdatasync
        // config library list` reports it as "DOES NOT RESOLVE" (confirmed: DbProviderFactories.GetFactory
        // throws ArgumentException for a type that isn't a DbProviderFactory, which that command's own
        // TryResolves already catches and reports gracefully, not a crash) — a known, harmless quirk of
        // reusing this schema for a library that was never shaped like the other seven entries here.
        new(
            "ikvm", "IKVM", "java.sql.Types, IKVM.Java",
            "IKVM (Java-on-.NET runtime)", "Runs a JDBC driver inside .NET for DbDataSync.Drivers.Jdbc's " +
                "reader-only JDBC source support.",
            PinnedVersion: "8.16.1"),
    ];

    /// <summary>Null when the package isn't in the starter table — the caller (the CLI) then requires
    /// <c>--factory-type</c> explicitly rather than guessing wrong.</summary>
    public static string? TryGet(string packageId) =>
        All.FirstOrDefault(e => string.Equals(e.PackageId, packageId, StringComparison.OrdinalIgnoreCase))?.FactoryType;

    /// <summary>Null when <paramref name="id"/> isn't a catalog id — the caller then treats the value
    /// as a literal package id instead (the pre-117 behaviour, still supported).</summary>
    public static LibraryCatalogEntry? TryGetById(string id) =>
        All.FirstOrDefault(e => string.Equals(e.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// <see cref="TryGetById"/>, falling back to a match on <see cref="LibraryCatalogEntry.PackageId"/>
    /// — an installed library can be keyed by either shape (a catalog install now resolves to the
    /// package id before installing; an older install, or one an operator named on the command line
    /// directly, can still be keyed by the catalog id), and a caller asking "is this id a known
    /// library, however it's keyed" needs both checked. Without this fallback, a caller that only
    /// tries <see cref="TryGetById"/> silently stops recognizing a library the moment it's installed
    /// under its package id instead of its catalog id — the exact regression this method exists to
    /// stop from recurring a third time; see
    /// architecture/planning/todo/follow-up-installed-catalog-libraries-stopped-showing-as-curated.md.
    /// </summary>
    public static LibraryCatalogEntry? TryGetByIdOrPackageId(string id) =>
        TryGetById(id) ?? All.FirstOrDefault(e => string.Equals(e.PackageId, id, StringComparison.OrdinalIgnoreCase));
}
