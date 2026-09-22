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
/// <param name="DriverJarPath">A literal filesystem path to the driver's jar. Provisional: real artifact
/// resolution for a shipped JDBC engine (<c>jars/</c> vs <c>libraries/</c>) is still
/// <c>architecture/planning/todo/jdbc-driver-support.md</c>'s own open question, not solved here — this
/// is the same shape the test project's own MSBuild-downloaded jar already is.</param>
public sealed record JdbcDriverSpec(
    string Id,
    SqlDialect Dialect,
    IDescriptorCatalog Catalog,
    string DriverClass,
    string DriverJarPath,
    IReadOnlyList<string> Readers,
    IReadOnlyList<string> Staging,
    IReadOnlyList<string> Writers,
    ISegmentValueBinder? ValueBinder = null,
    string? DisplayName = null) : IGenericDriverSpec;
