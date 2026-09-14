using Microsoft.Data.SqlClient;
using Npgsql;

namespace DbDataSync.State.Tests;

/// <summary>
/// A throwaway state database on a real server, for the cross-engine tests.
/// <para>
/// A whole database per run rather than a schema or a table prefix: the store's DDL names its tables
/// unqualified, which is exactly how a real deployment runs it, and a fixture that changed that would
/// be testing something the product does not do.
/// </para>
/// </summary>
public sealed class StateEngineFixture : IDisposable
{
    private static readonly string MsSqlServer =
        Environment.GetEnvironmentVariable("DBDATASYNC_TEST_MSSQL_SERVER")
        ?? "Data Source=localhost,14330;User ID=sa;Password=DbDataSync_Test_Pw1;TrustServerCertificate=True";

    private static readonly string PostgresServer =
        Environment.GetEnvironmentVariable("DBDATASYNC_TEST_POSTGRES_SERVER")
        ?? "Host=localhost;Port=15432;Username=dbdatasync;Password=DbDataSync_Test_Pw1;Database=postgres";

    private readonly string _engine;
    private readonly string _databaseName;

    public StateDatabase Database { get; }

    /// <param name="libraries">
    /// Phase 109g: MsSql/PostgresStateDialect resolve their connection through this rather than a
    /// direct package reference, so this fixture — which opens a real MsSql/Postgres connection twice
    /// over (once here for the CREATE/DROP DATABASE scaffolding, once inside StateDatabase itself) —
    /// has to register it against StateDialectRegistry.Default before either happens. See
    /// LibraryInstallFixture, which is what every caller here actually passes.
    /// </param>
    public StateEngineFixture(string engine, DbDataSync.Libraries.LibraryRegistry libraries)
    {
        _engine = engine;
        _databaseName = $"dbdatasync_state_{Guid.NewGuid():N}";
        StateDialectRegistry.Default.RegisterLibraryBackedEngines(libraries);

        switch (engine)
        {
            case StateEngineIds.MsSql:
                Execute(MsSqlServer, $"CREATE DATABASE [{_databaseName}];");
                Database = new StateDatabase(engine, MsSqlConnectionString(_databaseName));
                break;

            case StateEngineIds.Postgres:
                Execute(PostgresServer, $"CREATE DATABASE {_databaseName};");
                Database = new StateDatabase(engine, PostgresConnectionString(_databaseName));
                break;

            default:
                throw new ArgumentOutOfRangeException(
                    nameof(engine), engine, "This fixture stands up a server; SQLite needs no server.");
        }
    }

    public void Dispose()
    {
        try
        {
            if (_engine == StateEngineIds.MsSql)
            {
                Execute(MsSqlServer, $"ALTER DATABASE [{_databaseName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;");
                Execute(MsSqlServer, $"DROP DATABASE [{_databaseName}];");
            }
            else
            {
                // Npgsql pools by connection string, and Postgres refuses to drop a database with a
                // live session — including one of ours sitting idle in the pool.
                NpgsqlConnection.ClearAllPools();
                Execute(PostgresServer, $"DROP DATABASE IF EXISTS {_databaseName} WITH (FORCE);");
            }
        }
        catch (Exception)
        {
            // A scratch database left behind is untidy; a test run that fails in teardown after the
            // assertions passed is misleading. The name carries a GUID, so nothing collides.
        }
    }

    private static string MsSqlConnectionString(string database) =>
        new SqlConnectionStringBuilder(MsSqlServer) { InitialCatalog = database }.ConnectionString;

    private static string PostgresConnectionString(string database) =>
        new NpgsqlConnectionStringBuilder(PostgresServer) { Database = database }.ConnectionString;

    private void Execute(string serverConnectionString, string sql)
    {
        using var connection = StateDialect.For(_engine).CreateConnection(serverConnectionString);
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }
}
