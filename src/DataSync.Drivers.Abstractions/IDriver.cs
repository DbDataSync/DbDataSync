using System.Data.Common;
using DataSync.Core.Config;

namespace DataSync.Drivers.Abstractions;

/// <summary>
/// Identifies a database engine and advertises which readers/staging providers/writers it supports,
/// per architecture/detailed-design.md §3.4. Also owns connection creation and metadata introspection
/// so credential resolution (via ClrKernel.Core.Secrets.SecretStore) happens in exactly one place per
/// engine rather than being duplicated across every reader/writer.
/// </summary>
public interface IDriver
{
    ConnectionDriverType DriverType { get; }

    /// <summary>
    /// What this driver's connections take beyond the fields every connection has. Defaults to the
    /// free-form properties bag every driver has had since phase 3, so declaring nothing keeps the
    /// behaviour that was already there — a driver that wants named settings instead overrides it.
    /// </summary>
    IReadOnlyList<ParameterDescriptor> ConnectionParameters => [DriverParameters.ConnectionProperties];

    IReadOnlyList<IChangeReader> Readers { get; }
    IReadOnlyList<IStagingProvider> StagingProviders { get; }
    IReadOnlyList<IChangeWriter> Writers { get; }

    /// <summary>Opens a connection for the given config. <paramref name="credential"/> is the
    /// already-resolved plaintext secret (via SecretStore), or null for IntegratedAuth.</summary>
    DbConnection CreateConnection(ConnectionConfig connection, string? credential);

    Task<IReadOnlyList<string>> ListDatabasesAsync(DbConnection connection, CancellationToken cancellationToken);

    Task<IReadOnlyList<TableMetadata>> ListTablesAsync(
        DbConnection connection, string database, CancellationToken cancellationToken);

    Task<IReadOnlyList<ColumnMetadata>> ListColumnsAsync(
        DbConnection connection, string database, string schema, string table, CancellationToken cancellationToken);
}
