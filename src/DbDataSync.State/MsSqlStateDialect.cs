using System.Data.Common;
using DbDataSync.Core.Sql;
using Microsoft.Data.SqlClient;

namespace DbDataSync.State;

/// <summary>
/// SQL Server as the state store.
/// <para>
/// The engine that differs most, and every difference below is structural rather than cosmetic:
/// there is no <c>LIMIT</c>, no <c>ON CONFLICT</c>, and no indexable unbounded text. Those three
/// account for essentially all of this class.
/// </para>
/// </summary>
public sealed class MsSqlStateDialect : StateDialect
{
    /// <summary>
    /// The longest key an index can cover. SQL Server's limit is 900 bytes for a clustered key and
    /// 1700 for a non-clustered one; at two bytes per character, 450 is the value that is safe for
    /// both and is the same number the ASP.NET Core Identity schema settled on for the same reason.
    /// </summary>
    private const int MaxIndexableChars = 450;

    public static MsSqlStateDialect Instance { get; } = new();

    private MsSqlStateDialect() { }

    public override StateEngine Engine => StateEngine.MsSql;

    public override SqlDialect Sql => MsSqlDialect.Instance;

    public override DbConnection CreateConnection(string connectionString) => new SqlConnection(connectionString);

    public override string ParameterName(string name) => $"@{name}";

    /// <summary>
    /// <c>OFFSET/FETCH</c> rather than <c>TOP</c>, because it takes a parameter — <c>TOP</c> takes one
    /// too but only in parentheses, and this reads as what it is. It requires an <c>ORDER BY</c>,
    /// which every limited query here already has.
    /// </summary>
    public override string Limit(string parameterName) =>
        $"OFFSET 0 ROWS FETCH NEXT {Parameter(parameterName)} ROWS ONLY";

    /// <summary>
    /// SQL Server has no <c>ON CONFLICT</c>. <c>MERGE</c> is the equivalent that keeps this to one
    /// statement — and one statement matters here, because the callers relying on this are relying on
    /// it being atomic: two racing workers must not both believe they claimed the same lock.
    /// <para>
    /// <c>HOLDLOCK</c> is not optional. Without it MERGE's existence check takes a shared lock that is
    /// released before the insert, which leaves exactly the race the idiom exists to prevent.
    /// </para>
    /// </summary>
    /// <summary>
    /// A partial index has no equivalent in MERGE's conflict handling, so its predicate joins the
    /// match condition instead — which is the same question asked the same way: does a row that would
    /// collide already exist. Written against <c>target</c>, because it is a fact about the stored row.
    /// </summary>
    public override string InsertOrIgnore(
        string table, string columns, string values, string? conflictTarget, string? conflictWhere = null) =>
        Merge(table, columns, values, conflictTarget ?? RequireTarget(table), matched: null, conflictWhere);

    public override string Upsert(
        string table, string columns, string values, string conflictTarget, string updates) =>
        Merge(table, columns, values, conflictTarget, matched: updates.Replace("EXCLUDED.", "source."));

    private static string Merge(
        string table, string columns, string values, string conflictTarget, string? matched,
        string? conflictWhere = null)
    {
        var columnNames = Split(columns);
        var on = string.Join(
            " AND ", Split(conflictTarget).Select(c => $"target.{c} = source.{c}"));
        if (conflictWhere is not null)
            on += $" AND ({Qualify(conflictWhere)})";

        return $"""
            MERGE {table} WITH (HOLDLOCK) AS target
            USING (VALUES ({values})) AS source ({string.Join(", ", columnNames)})
            ON {on}
            {(matched is null ? "" : $"WHEN MATCHED THEN UPDATE SET {matched}")}
            WHEN NOT MATCHED THEN INSERT ({columns}) VALUES ({string.Join(", ", columnNames.Select(c => $"source.{c}"))});
            """;
    }

    private static string[] Split(string list) => [.. list.Split(',', StringSplitOptions.TrimEntries)];

    /// <summary>
    /// Points a partial index's predicate at the stored row. The predicate is written unqualified in
    /// the schema (it is an index definition, where there is only one table); inside a MERGE there are
    /// two, and an unqualified column reference would be ambiguous.
    /// <para>
    /// **String literals are skipped, and that is not a detail.** The work queue's predicate is
    /// <c>Status IN ('Pending','Claimed','Running')</c>, and a naive word-substitution turns the
    /// *values* into <c>target.Pending</c> — which is still valid SQL if a column happens to exist,
    /// and otherwise fails somewhere far from the cause. The first version of this did exactly that,
    /// and the cross-engine queue test is what caught it.
    /// </para>
    /// </summary>
    private static string Qualify(string predicate) =>
        System.Text.RegularExpressions.Regex.Replace(
            predicate,
            // A quoted literal, or a bare word that is not a function call. Matching literals first
            // and returning them untouched is what keeps the substitution out of them.
            @"'(?:[^']|'')*'|\b([A-Za-z_]\w*)\b(?!\s*\()",
            match =>
                match.Value.StartsWith('\'') || Keywords.Contains(match.Value.ToUpperInvariant())
                    ? match.Value
                    : $"target.{match.Value}");

    private static readonly HashSet<string> Keywords =
        ["IN", "AND", "OR", "NOT", "NULL", "IS", "LIKE", "BETWEEN", "EXISTS"];

    /// <summary>
    /// The other two engines allow <c>ON CONFLICT DO NOTHING</c> with no target, meaning "any unique
    /// constraint". MERGE has no such form — it needs to be told what to match on — so a caller
    /// leaving the target null has to be a caller this engine cannot serve, and saying so is better
    /// than merging on nothing and silently updating every row in the table.
    /// </summary>
    private static string RequireTarget(string table) =>
        throw new NotSupportedException(
            $"An insert-or-ignore against '{table}' did not name its conflicting columns. SQL Server's " +
            "MERGE has no untargeted form, so the columns have to be stated explicitly.");

    public override string IdentityKey(string column) => $"{column} BIGINT IDENTITY(1,1) PRIMARY KEY";

    public override string Text => "NVARCHAR(MAX)";

    /// <summary>Bounded, because SQL Server cannot index <c>NVARCHAR(MAX)</c> — see
    /// <see cref="StateDialect.Text"/>. Every value this schema puts in a key is a name, an id, a
    /// GUID or a hash; none comes close to this.</summary>
    public override string KeyText => $"NVARCHAR({MaxIndexableChars})";

    public override string Integer => "BIGINT";

    public override string AddColumn => "ADD";

    /// <summary>Index names here are scoped to a table rather than to the schema, so dropping one
    /// requires saying which table it belongs to.</summary>
    public override string DropIndex(string index, string table) => $"DROP INDEX {index} ON {table}";

    public override int GetSchemaVersion(DbConnection connection)
    {
        using var ensure = connection.CreateCommand();
        ensure.CommandText = """
            IF OBJECT_ID('SchemaVersion', 'U') IS NULL
                CREATE TABLE SchemaVersion (Version INT NOT NULL);
            """;
        ensure.ExecuteNonQuery();

        using var read = connection.CreateCommand();
        read.CommandText = "SELECT TOP 1 Version FROM SchemaVersion;";
        return read.ExecuteScalar() is { } value and not DBNull ? Convert.ToInt32(value) : 0;
    }

    public override void SetSchemaVersion(DbConnection connection, int version)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            UPDATE SchemaVersion SET Version = @version;
            IF @@ROWCOUNT = 0 INSERT INTO SchemaVersion (Version) VALUES (@version);
            """;
        var parameter = cmd.CreateParameter();
        parameter.ParameterName = "@version";
        parameter.Value = version;
        cmd.Parameters.Add(parameter);
        cmd.ExecuteNonQuery();
    }
}
