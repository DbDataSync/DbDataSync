using System.Data.Common;
using DataSync.Core.Sql;
using Npgsql;

namespace DataSync.State;

/// <summary>
/// PostgreSQL as the state store.
/// <para>
/// The closest of the three to SQLite for this purpose: <c>ON CONFLICT</c> is Postgres's own syntax
/// that SQLite adopted, and <c>LIMIT</c> is shared. What differs is identity DDL and the schema
/// version having to live in a table.
/// </para>
/// </summary>
public sealed class PostgresStateDialect : StateDialect
{
    public static PostgresStateDialect Instance { get; } = new();

    private PostgresStateDialect() { }

    public override StateEngine Engine => StateEngine.Postgres;

    public override SqlDialect Sql => PostgresDialect.Instance;

    public override DbConnection CreateConnection(string connectionString) => new NpgsqlConnection(connectionString);

    public override string ParameterName(string name) => $"@{name}";

    public override string Limit(string parameterName) => $"LIMIT {Parameter(parameterName)}";

    /// <summary>The partial-index predicate is written into the conflict clause, which is how both
    /// Postgres and SQLite are told *which* index to infer when a table has more than one.</summary>
    public override string InsertOrIgnore(
        string table, string columns, string values, string? conflictTarget, string? conflictWhere = null) =>
        $"INSERT INTO {table} ({columns}) VALUES ({values}) " +
        $"ON CONFLICT{(conflictTarget is null ? "" : $" ({conflictTarget})")}" +
        $"{(conflictWhere is null ? "" : $" WHERE {conflictWhere}")} DO NOTHING;";

    public override string Upsert(
        string table, string columns, string values, string conflictTarget, string updates) =>
        $"INSERT INTO {table} ({columns}) VALUES ({values}) " +
        $"ON CONFLICT ({conflictTarget}) DO UPDATE SET {updates};";

    /// <summary><c>GENERATED ALWAYS</c> rather than <c>SERIAL</c>: the standard spelling, and the one
    /// that refuses an explicit value rather than quietly accepting one and desynchronising the
    /// sequence. Nothing here ever supplies its own id.</summary>
    public override string IdentityKey(string column) =>
        $"{column} BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY";

    public override string Text => "TEXT";

    // Postgres indexes TEXT without a length, so there is nothing to bound — see StateDialect.Text
    // for the engine that cannot.
    public override string KeyText => "TEXT";

    public override string Integer => "BIGINT";

    public override int GetSchemaVersion(DbConnection connection)
    {
        using var ensure = connection.CreateCommand();
        ensure.CommandText = "CREATE TABLE IF NOT EXISTS SchemaVersion (Version INT NOT NULL);";
        ensure.ExecuteNonQuery();

        using var read = connection.CreateCommand();
        read.CommandText = "SELECT Version FROM SchemaVersion LIMIT 1;";
        return read.ExecuteScalar() is { } value and not DBNull ? Convert.ToInt32(value) : 0;
    }

    public override void SetSchemaVersion(DbConnection connection, int version)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            UPDATE SchemaVersion SET Version = @version;
            INSERT INTO SchemaVersion (Version)
            SELECT @version WHERE NOT EXISTS (SELECT 1 FROM SchemaVersion);
            """;
        var parameter = cmd.CreateParameter();
        parameter.ParameterName = "@version";
        parameter.Value = version;
        cmd.Parameters.Add(parameter);
        cmd.ExecuteNonQuery();
    }
}
