using System.Data.Common;
using DbDataSync.Core.Sql;

namespace DbDataSync.State.Tests;

/// <summary>
/// A fixture <see cref="StateDialect"/> that renders SQLite-shaped SQL under a made-up engine id —
/// everything delegates to <see cref="SqliteStateDialect.Instance"/> except <see cref="Engine"/>, which
/// is the one thing a real custom dialect could not just reuse (the built-ins are sealed with private
/// constructors, so this composes rather than subclasses).
/// </summary>
public sealed class FixtureStateDialect : StateDialect
{
    public const string EngineId = "fixture.sqlite";

    private static readonly SqliteStateDialect Inner = SqliteStateDialect.Instance;

    public override string Engine => EngineId;
    public override SqlDialect Sql => Inner.Sql;
    public override DbConnection CreateConnection(string connectionString) => Inner.CreateConnection(connectionString);
    public override string ParameterName(string name) => Inner.ParameterName(name);
    public override string Limit(string parameterName) => Inner.Limit(parameterName);
    public override string InsertOrIgnore(
        string table, string columns, string values, string? conflictTarget, string? conflictWhere = null) =>
        Inner.InsertOrIgnore(table, columns, values, conflictTarget, conflictWhere);
    public override string Upsert(string table, string columns, string values, string conflictTarget, string updates) =>
        Inner.Upsert(table, columns, values, conflictTarget, updates);
    public override string IdentityKey(string column) => Inner.IdentityKey(column);
    public override string Text => Inner.Text;
    public override string KeyText => Inner.KeyText;
    public override string Integer => Inner.Integer;
    public override int GetSchemaVersion(DbConnection connection) => Inner.GetSchemaVersion(connection);
    public override void SetSchemaVersion(DbConnection connection, int version) => Inner.SetSchemaVersion(connection, version);
    public override void OnConnectionOpened(DbConnection connection) => Inner.OnConnectionOpened(connection);
    public override bool ShouldRetry(Exception exception) => Inner.ShouldRetry(exception);
}

public sealed class StateDialectRegistryTests
{
    [Theory]
    [InlineData(StateEngineIds.Sqlite, typeof(SqliteStateDialect))]
    [InlineData(StateEngineIds.MsSql, typeof(MsSqlStateDialect))]
    [InlineData(StateEngineIds.Postgres, typeof(PostgresStateDialect))]
    public void TheThreeBuiltIns_ResolveByTheirStringId(string engine, Type expectedType)
    {
        var dialect = StateDialect.For(engine);

        Assert.IsType(expectedType, dialect);
        Assert.Equal(engine, dialect.Engine);
    }

    [Fact]
    public void AnUnknownId_ThrowsNamingTheBuiltIns()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => StateDialect.For("not.a.real.engine"));

        Assert.Contains("not.a.real.engine", ex.Message);
        Assert.Contains(StateEngineIds.Sqlite, ex.Message);
        Assert.Contains(StateEngineIds.MsSql, ex.Message);
        Assert.Contains(StateEngineIds.Postgres, ex.Message);
    }

    [Fact]
    public void TryGet_ForAnUnregisteredId_ReturnsFalseRatherThanThrowing()
    {
        var registry = new StateDialectRegistry();

        Assert.False(registry.TryGet(StateEngineIds.Sqlite, out _));
    }

    [Fact]
    public void ARegisteredCustomDialect_ResolvesByItsOwnId()
    {
        var registry = new StateDialectRegistry();
        var fixture = new FixtureStateDialect();

        registry.Register(fixture);

        Assert.Same(fixture, registry.Get(FixtureStateDialect.EngineId));
    }

    /// <summary>The real point of the fixture: registered into the process-wide default (the same one
    /// every built-in lives in, and the one <see cref="StateDialect.For"/> itself reads), a
    /// <see cref="StateDatabase"/> constructed against it runs the whole migration set exactly as it
    /// would against real SQLite — proof this is a genuine extension point, not just a lookup table.</summary>
    [Fact]
    public void ARegisteredCustomDialect_RunsTheFullMigrationSet_ThroughStateDatabase()
    {
        StateDialectRegistry.Default.Register(new FixtureStateDialect());
        var dbPath = Path.Combine(Directory.CreateTempSubdirectory("dbdatasync-fixture-dialect-").FullName, "state.db");

        var database = new StateDatabase(FixtureStateDialect.EngineId, $"Data Source={dbPath}");

        using var connection = database.OpenConnection();
        using var cmd = database.Command(connection, "SELECT COUNT(*) FROM Tasks;");
        Assert.Equal(0, Convert.ToInt32(cmd.ExecuteScalar()));
    }
}
