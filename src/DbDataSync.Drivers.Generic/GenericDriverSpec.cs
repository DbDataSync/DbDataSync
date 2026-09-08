using System.Data.Common;
using DbDataSync.Core.Sql;

namespace DbDataSync.Drivers.Generic;

/// <summary>
/// The connection-string key spellings a <see cref="GenericDriver"/> assembles its connection string
/// from. A plain <see cref="DbConnectionStringBuilder"/> — not a provider-typed one — accepts any key
/// through its indexer, so the driver never needs the provider's own builder type; only the key names
/// vary per engine (Npgsql's <c>Timeout</c> vs SqlClient's <c>Connect Timeout</c>).
/// </summary>
/// <param name="IntegratedSecurity">The key that turns on the provider's own integrated-auth
/// negotiation, or null for an engine with none (Postgres has no such flag — GSSAPI/peer auth is
/// negotiated from a username alone).</param>
public sealed record GenericConnectionStringKeys(
    string Host = "Host",
    string? Port = "Port",
    string Database = "Database",
    string Username = "User Id",
    string Password = "Password",
    string ConnectTimeout = "Connect Timeout",
    string? IntegratedSecurity = null);

/// <summary>
/// Everything a <see cref="GenericDriver"/> needs to stand up against one SQL engine: a dialect, a
/// provider factory, a catalog, which of <c>DbDataSync.Drivers.Generic</c>'s engine-neutral
/// readers/staging/writers to register, and the connection-string shape. Phase 109a's "a new engine is
/// a dialect, a connection factory and a catalog" made concrete and constructible, rather than
/// re-derived per compiled driver project.
/// </summary>
/// <param name="Id">The driver id this spec stands up — <see cref="GenericDriver.DriverType"/>.</param>
/// <param name="Catalog"><see cref="InformationSchemaQueries"/> specifically, not the narrower
/// <see cref="ITableCatalog"/>: <see cref="IDriver.ListTablesAsync"/> and
/// <see cref="IDriver.ListDatabasesAsync"/> need more than that interface declares, and
/// <c>information_schema</c> is the only catalog strategy this phase supports. A different strategy
/// (Oracle's <c>ALL_*</c> views, ODBC's <c>GetSchema</c>) is a later phase's problem.</param>
/// <param name="Readers">Which of <see cref="GenericDriverKinds"/>'s reader Kinds to register:
/// <see cref="GenericDriverKinds.Watermark"/>, <see cref="GenericDriverKinds.BatchReload"/>,
/// <see cref="GenericDriverKinds.TriggerAudit"/>.</param>
/// <param name="Staging">Which staging Kinds to register — only
/// <see cref="GenericDriverKinds.StagingTable"/> exists today.</param>
/// <param name="Writers">Which writer Kinds to register:
/// <see cref="GenericDriverKinds.DeleteInsert"/>, <see cref="GenericDriverKinds.Snapshot"/>,
/// <see cref="GenericDriverKinds.Scd2"/>.</param>
/// <param name="ValueBinder">Types a segment bound to the column it is compared against — see
/// <see cref="ISegmentValueBinder"/>. Null falls back to <see cref="GenericValueBinder"/>, which binds
/// through <see cref="SqlDialect.ToCanonicalType"/> and a generic <see cref="System.Data.DbType"/>
/// rather than a provider-specific type enum — a real loss of precision for an engine with legitimate
/// provider-typed bindings, but the only thing possible without engine-specific code, and the reason a
/// value-aware type (Oracle's <c>NUMBER</c>) is a compiled driver's job, not a descriptor's.</param>
/// <param name="DefaultDatabase">What to connect to when neither <see cref="System.Data.Common.DbConnectionStringBuilder"/>
/// mode supplies one — SQL Server's <c>master</c>, Postgres's <c>postgres</c>. Required because
/// "connect to browse databases" needs some starting database on every server engine.</param>
/// <param name="DisplayName">What an operator sees in the connection editor's engine picker
/// (<c>GET /api/drivers</c>, phase 109d) — a descriptor's own <c>displayName</c>. Null falls back to
/// <see cref="Id"/>, same as every built-in driver's <see cref="IDriver.DisplayName"/> default.</param>
public sealed record GenericDriverSpec(
    string Id,
    SqlDialect Dialect,
    DbProviderFactory ProviderFactory,
    InformationSchemaQueries Catalog,
    IReadOnlyList<string> Readers,
    IReadOnlyList<string> Staging,
    IReadOnlyList<string> Writers,
    GenericConnectionStringKeys ConnectionStringKeys,
    string DefaultDatabase,
    int? DefaultPort = null,
    ISegmentValueBinder? ValueBinder = null,
    string? DisplayName = null)
{
    /// <summary>The common shape: every generic Kind, <c>information_schema</c> catalog, default
    /// connection-string keys. What most descriptor-shaped engines want; override individual
    /// parameters (with-expressions) for one that doesn't.</summary>
    public static GenericDriverSpec InformationSchema(
        string id, SqlDialect dialect, DbProviderFactory factory, string defaultDatabase, int? defaultPort = null) =>
        new(
            id,
            dialect,
            factory,
            new InformationSchemaQueries(dialect),
            Readers: [GenericDriverKinds.Watermark, GenericDriverKinds.BatchReload, GenericDriverKinds.TriggerAudit],
            Staging: [GenericDriverKinds.StagingTable],
            Writers: [GenericDriverKinds.DeleteInsert, GenericDriverKinds.Snapshot, GenericDriverKinds.Scd2],
            ConnectionStringKeys: new GenericConnectionStringKeys(),
            DefaultDatabase: defaultDatabase,
            DefaultPort: defaultPort);
}
