using DbDataSync.Core.Sql;
using DbDataSync.Drivers.Abstractions;
using DbDataSync.Drivers.Generic;

namespace DbDataSync.Drivers.Jdbc;

/// <summary>
/// <see cref="IGenericDriverSpec"/>'s JDBC counterpart to <see cref="GenericDriverSpec"/> — phase 168V.
/// Carries what a JDBC-backed engine needs that an ADO.NET one has no notion of (which driver class to
/// load, from which jar) instead of <see cref="GenericDriverSpec.ProviderFactory"/>.
/// <para>
/// Phase 175M: does, after all, reuse <see cref="GenericDriverSpec.ConnectionStringKeys"/>'s own type
/// (<see cref="ConnectionStringKeys"/> below) — the final JDBC URL is still a positional, per-vendor
/// string with no key/value shape, but the *intermediate* representation this record's own
/// <see cref="UrlTemplate"/>/host/port/database/username unification step builds never leaves this
/// codebase (it's not handed to a real provider), so its key spellings are free to be whatever's
/// convenient — including the literal <c>java.util.Properties</c> names (<c>user</c>/<c>password</c>) a
/// JDBC driver will eventually receive. One mechanism, one type, no second mapping.
/// </para>
/// </summary>
/// <param name="DriverClass">The JDBC driver's fully-qualified Java class name — <c>org.postgresql.Driver</c>,
/// for pgJDBC.</param>
/// <param name="DriverJarPaths">Phase 169V — real, resolved filesystem paths to the driver's jar(s), one
/// element for the common single-jar case, more for a driver that ships split across several (Oracle's
/// wallet support, Db2's license jar). A direct construction (the test projects' own shape) can pass any
/// literal path; <see cref="JdbcGenericDriver.FromDescriptor"/> resolves a <c>driver.yaml</c>'s own
/// <c>driverJarPaths</c> — names inside <c>&lt;repo&gt;/files/</c>, not paths — into this shape before
/// constructing a <see cref="JdbcDriverSpec"/>, the same "id/name in, real path out" resolution
/// <c>LibraryRegistry.GetFactory</c> already does for <see cref="GenericDriverSpec.ProviderFactory"/>'s
/// own <c>library:</c> reference.</param>
/// <param name="UrlTemplate">Phase 175M. <c>{host}</c>/<c>{port}</c>/<c>{database}</c>/<c>{username}</c>
/// placeholders, e.g. <c>"jdbc:postgresql://{host}:{port}/{database}"</c> — each one substituted if
/// present in the template, or carried as a JDBC property under <see cref="ConnectionStringKeys"/>'s own
/// key name if the template doesn't reference it, so a resolved value is never silently dropped. Never
/// <c>{password}</c> — a credential is always a property, never a template placeholder (matching the
/// "never in the URL" rule <see cref="DbDataSync.Core.Config.ConnectionConfig.ConnectionString"/>'s own
/// doc comment already documents). Null (the pre-175M default) preserves the original contract exactly: an operator
/// must supply the complete, final JDBC URL via <c>ConnectionConfig.ConnectionString</c> — see
/// <see cref="JdbcGenericDriver.CreateConnection"/>'s own check.</param>
/// <param name="ConnectionStringKeys">Defaults to <see cref="JdbcGenericDriver.DefaultConnectionStringKeys"/>
/// (<c>host</c>/<c>port</c>/<c>database</c>/<c>user</c>/<c>password</c> — the literal
/// <c>java.util.Properties</c> names) rather than <see cref="GenericConnectionStringKeys"/>'s own
/// ADO.NET-flavoured defaults (<c>Host</c>/<c>User Id</c>/…), which would be the wrong spelling for a
/// property a real JDBC driver reads.</param>
public sealed record JdbcDriverSpec(
    string Id,
    SqlDialect Dialect,
    IDescriptorCatalog Catalog,
    string DriverClass,
    IReadOnlyList<string> DriverJarPaths,
    IReadOnlyList<string> Readers,
    IReadOnlyList<string> Staging,
    IReadOnlyList<string> Writers,
    string? UrlTemplate = null,
    GenericConnectionStringKeys? ConnectionStringKeys = null,
    ISegmentValueBinder? ValueBinder = null,
    string? DisplayName = null) : IGenericDriverSpec;
