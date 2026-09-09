using System.Data.Common;
using DbDataSync.Core.Config;
using DbDataSync.Drivers.Abstractions;
using Microsoft.Data.Sqlite;

namespace DbDataSync.Drivers.LoaderTestFixture;

/// <summary>
/// A minimal <see cref="IDriver"/> — the fixture <c>DriverLoader.LoadCompiledDrivers</c>'s tests load
/// as a real compiled plugin. Wraps <c>Microsoft.Data.Sqlite</c> because it needs no server, no
/// container, and no install step of its own — <see cref="ConnectionConfig.ConnectionString"/> is
/// taken as a literal SQLite connection string verbatim.
/// </summary>
public sealed class FixtureDriver : IDriver
{
    public string DriverType => "fixture.loadertest";

    public IReadOnlyList<IChangeReader> Readers { get; } = [];
    public IReadOnlyList<IStagingProvider> StagingProviders { get; } = [];
    public IReadOnlyList<IChangeWriter> Writers { get; } = [];

    public DbConnection CreateConnection(ConnectionConfig connection, string? credential) =>
        new SqliteConnection(connection.ConnectionString ?? "Data Source=:memory:");

    public Task<IReadOnlyList<string>> ListDatabasesAsync(DbConnection connection, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<string>>([]);

    public Task<IReadOnlyList<TableMetadata>> ListTablesAsync(
        DbConnection connection, string database, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<TableMetadata>>([]);

    public Task<IReadOnlyList<ColumnMetadata>> ListColumnsAsync(
        DbConnection connection, string database, string schema, string table, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<ColumnMetadata>>([]);

    /// <summary>Proves the loader actually instantiated and can drive this plugin, beyond "it
    /// registered" — opens the connection it was handed and reads one value back.</summary>
    public async Task<long> RoundTripAsync(DbConnection connection, CancellationToken cancellationToken)
    {
        await connection.OpenAsync(cancellationToken);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT 1;";
        return (long)(await cmd.ExecuteScalarAsync(cancellationToken))!;
    }
}

/// <summary>Exists only so a test can name a real type in this assembly that isn't an <see cref="IDriver"/>.</summary>
public sealed class NotADriver;

/// <summary>The same driver, declaring a contract version the host cannot support — proves the loader
/// refuses it rather than instantiating a plugin built against an incompatible <c>IDriver</c> shape.</summary>
public sealed class FixtureDriverBadContract : IDriver
{
    public string DriverType => "fixture.badcontract";
    public int ContractVersion => 999;

    public IReadOnlyList<IChangeReader> Readers { get; } = [];
    public IReadOnlyList<IStagingProvider> StagingProviders { get; } = [];
    public IReadOnlyList<IChangeWriter> Writers { get; } = [];

    public DbConnection CreateConnection(ConnectionConfig connection, string? credential) =>
        throw new NotSupportedException();

    public Task<IReadOnlyList<string>> ListDatabasesAsync(DbConnection connection, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public Task<IReadOnlyList<TableMetadata>> ListTablesAsync(
        DbConnection connection, string database, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public Task<IReadOnlyList<ColumnMetadata>> ListColumnsAsync(
        DbConnection connection, string database, string schema, string table, CancellationToken cancellationToken) =>
        throw new NotSupportedException();
}
