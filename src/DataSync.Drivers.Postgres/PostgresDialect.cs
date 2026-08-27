using System.Data.Common;
using DataSync.Drivers.Generic;

namespace DataSync.Drivers.Postgres;

/// <summary>
/// PostgreSQL's answers to the mechanical variations in <see cref="SqlDialect"/>.
/// <para>
/// Two of them are not mechanical at all and are worth naming: a Postgres connection cannot change
/// database, and an <c>INSERT</c> supplying a <c>GENERATED ALWAYS AS IDENTITY</c> column needs
/// <c>OVERRIDING SYSTEM VALUE</c> written into the statement rather than a session flag around it.
/// Both are exactly the hooks phase 18 defined, which is the evidence they were the right seams.
/// </para>
/// </summary>
public sealed class PostgresDialect : SqlDialect
{
    public static PostgresDialect Instance { get; } = new();

    private PostgresDialect() { }

    public override string QuoteIdentifier(string identifier) => $"\"{identifier.Replace("\"", "\"\"")}\"";

    public override string ParameterReference(string name) => $"@{name}";

    /// <summary>Postgres allows 65535 bound parameters, so batches are far larger than SQL Server's.</summary>
    public override int MaxParametersPerStatement => 65535;

    public override string OperationMarkerColumnType => "char(1)";

    /// <summary>
    /// A Postgres connection is bound to one database for its lifetime — <c>ChangeDatabase</c> would
    /// have to close and reopen it, which silently discards the transaction and any open cursor the
    /// caller is streaming from. So this validates instead: the connection is already pointed at its
    /// database by the connection string, and a mapping asking for a different one is a configuration
    /// error worth saying out loud rather than a reconnect worth performing.
    /// </summary>
    public override Task UseDatabaseAsync(DbConnection connection, string database, CancellationToken cancellationToken)
    {
        if (!string.Equals(connection.Database, database, StringComparison.Ordinal))
            throw new InvalidOperationException(
                $"This connection is bound to database '{connection.Database}', but '{database}' was requested. " +
                "A PostgreSQL connection cannot change database — configure a separate DataSync connection " +
                "for each database.");

        return Task.CompletedTask;
    }

    /// <summary>
    /// <c>OVERRIDING SYSTEM VALUE</c> belongs to a column generated <c>ALWAYS</c>; a <c>BY DEFAULT</c>
    /// identity and an old-style <c>serial</c> accept an explicit value without it, which is what
    /// <see cref="PostgresCatalog"/> encodes when it sets <c>IsIdentity</c>.
    /// </summary>
    public override string RenderInsertInto(string qualifiedTable, string columnList, bool overrideGenerated) =>
        overrideGenerated
            ? $"INSERT INTO {qualifiedTable} ({columnList}) OVERRIDING SYSTEM VALUE"
            : $"INSERT INTO {qualifiedTable} ({columnList})";

    /// <summary>
    /// A pass-through: the override is part of the statement (see <see cref="RenderInsertInto"/>), not
    /// a setting around it. Which is exactly why phase 18 made this hook "run this write" rather than
    /// "give me a clause" — the two engines put the same intent in different places.
    /// </summary>
    public override Task<T> WriteWithGeneratedColumnOverrideAsync<T>(
        DbConnection connection,
        DbTransaction? transaction,
        string qualifiedTable,
        bool overrideRequired,
        Func<Task<T>> write,
        CancellationToken cancellationToken) => write();

    public override BucketableKind ClassifyForBucketing(string baseTypeName) => baseTypeName switch
    {
        "int2" or "int4" or "int8" or "serial" or "bigserial" or "smallserial" => BucketableKind.Integral,
        "float4" or "float8" or "money" => BucketableKind.Numeric,
        "timestamp without time zone" or "timestamp with time zone" => BucketableKind.DateTime,
        "timestamptz" => BucketableKind.DateTimeOffset,
        _ => base.ClassifyForBucketing(baseTypeName),
    };
}
