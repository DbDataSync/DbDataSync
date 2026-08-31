using Microsoft.Data.SqlClient;
using Npgsql;

namespace DataSync.State.Tests;

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
        Environment.GetEnvironmentVariable("DATASYNC_TEST_MSSQL_SERVER")
        ?? "Data Source=localhost,14330;User ID=sa;Password=DataSync_Test_Pw1;TrustServerCertificate=True";

    private static readonly string PostgresServer =
        Environment.GetEnvironmentVariable("DATASYNC_TEST_POSTGRES_SERVER")
        ?? "Host=localhost;Port=15432;Username=datasync;Password=DataSync_Test_Pw1;Database=postgres";

    private readonly StateEngine _engine;
    private readonly string _databaseName;

    public StateDatabase Database { get; }

    public StateEngineFixture(StateEngine engine)
    {
        _engine = engine;
        _databaseName = $"datasync_state_{Guid.NewGuid():N}";

        switch (engine)
        {
            case StateEngine.MsSql:
                Execute(MsSqlServer, $"CREATE DATABASE [{_databaseName}];");
                Database = new StateDatabase(engine, MsSqlConnectionString(_databaseName));
                break;

            case StateEngine.Postgres:
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
            if (_engine == StateEngine.MsSql)
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
