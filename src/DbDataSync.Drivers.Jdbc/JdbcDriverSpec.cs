using DbDataSync.Core.Sql;
using DbDataSync.Drivers.Abstractions;
using DbDataSync.Drivers.Generic;

namespace DbDataSync.Drivers.Jdbc;

/// <summary>
/// <see cref="IGenericDriverSpec"/>'s JDBC counterpart to <see cref="GenericDriverSpec"/> — phase 168V.
/// Carries what a JDBC-backed engine needs that an ADO.NET one has no notion of (which driver class to
/// load, from which jar) instead of <see cref="GenericDriverSpec.ProviderFactory"/>/
/// <see cref="GenericDriverSpec.ConnectionStringKeys"/>, which JDBC has no equivalent of at all — a JDBC
/// connection is addressed by a URL (<c>ConnectionConfig.ConnectionString</c>,
/// <c>AddressMode.connectionString</c>), not assembled from host/port/database.
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
public sealed record JdbcDriverSpec(
    string Id,
    SqlDialect Dialect,
    IDescriptorCatalog Catalog,
    string DriverClass,
    IReadOnlyList<string> DriverJarPaths,
    IReadOnlyList<string> Readers,
    IReadOnlyList<string> Staging,
    IReadOnlyList<string> Writers,
    ISegmentValueBinder? ValueBinder = null,
    string? DisplayName = null) : IGenericDriverSpec;
