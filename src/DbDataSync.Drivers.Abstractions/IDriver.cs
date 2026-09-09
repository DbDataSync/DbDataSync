using System.Data.Common;
using DbDataSync.Core.Config;

namespace DbDataSync.Drivers.Abstractions;

/// <summary>
/// Identifies a database engine and advertises which readers/staging providers/writers it supports,
/// per architecture/detailed-design.md §3.4. Also owns connection creation and metadata introspection
/// so credential resolution (via ClrKernel.Core.Secrets.SecretStore) happens in exactly one place per
/// engine rather than being duplicated across every reader/writer.
/// </summary>
public interface IDriver
{
    string DriverType { get; }

    /// <summary>What an operator sees in the connection editor's engine picker. Defaults to
    /// <see cref="DriverType"/> for every built-in — none has ever needed anything friendlier — so a
    /// descriptor driver (which does: <c>driver.yaml</c>'s <c>displayName</c>, phase 109d) is the only
    /// one that overrides it.</summary>
    string DisplayName => DriverType;

    /// <summary>
    /// Which version of this interface's contract a driver was built against. Every existing built-in
    /// (and every descriptor-defined <c>GenericDriver</c>, 109d) is version 1 by this default — the
    /// default exists so none of them needed a change the day this was added. A compiled plugin loaded
    /// from a package (phase 109e) is the one thing that ever states a different value: the loader
    /// refuses one outside <c>[MinSupportedContractVersion, CurrentContractVersion]</c> with a message
    /// naming which DbDataSync version to target, rather than letting a stale plugin fail cryptically
    /// the first time it is actually used.
    /// </summary>
    int ContractVersion => 1;

    /// <summary>
    /// Everything this driver's connections take — addressing, authentication, database, and the
    /// free-form properties bag — with each parameter's visibility already decided from
    /// <paramref name="values"/>.
    /// <para>
    /// A method rather than a property because the answer depends on the answers: once an operator
    /// picks connection-string addressing, Host is not a setting with an empty value, it is not a
    /// setting. Deciding that here rather than in the form keeps the rule with the thing that owns it,
    /// and means a new driver with different addressing needs no change to the connection screen.
    /// </para>
    /// </summary>
    IReadOnlyList<ParameterDescriptor> ConnectionParameters(IReadOnlyDictionary<string, string> values) =>
        DriverParameters.ForConnection(values, DefaultPort);

    /// <summary>The port this engine listens on unless told otherwise, pre-filled on a new connection.
    /// Null for a driver that has no such notion — ODBC through a DSN, for instance.</summary>
    int? DefaultPort => null;

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
