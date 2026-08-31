using System.Data.Common;
using DataSync.Core.Sql;

namespace DataSync.State;

/// <summary>
/// Everything the state store needs to know about the engine underneath it — see phase 63.
/// <para>
/// **Separate from <see cref="SqlDialect"/> rather than piled onto it, on that class's own
/// instruction.** Its doc says plainly that anything differing *structurally* — "bulk loading, upsert
/// syntax, identity handling" — belongs in an engine-specific implementation rather than in the
/// dialect, and that if generalising something there would need a flag per engine, that is the signal
/// it does not belong there. Upsert syntax, identity DDL and row limiting are exactly that list. So
/// this composes a <see cref="SqlDialect"/> for the mechanical parts (quoting, parameter spelling) and
/// owns the structural ones itself.
/// </para>
/// <para>
/// It is also the honest scope: <see cref="SqlDialect"/> serves replication, where the schemas belong
/// to somebody else and the type mapping has to be general. The state store's schema is fixed and
/// internal — ten tables this project wrote — so what it needs is a much smaller and much more
/// opinionated surface than a driver's.
/// </para>
/// </summary>
public abstract class StateDialect
{
    public static StateDialect For(StateEngine engine) => engine switch
    {
        StateEngine.Sqlite => SqliteStateDialect.Instance,
        StateEngine.MsSql => MsSqlStateDialect.Instance,
        StateEngine.Postgres => PostgresStateDialect.Instance,
        _ => throw new ArgumentOutOfRangeException(nameof(engine), engine, "Unknown state engine."),
    };

    public abstract StateEngine Engine { get; }

    /// <summary>The mechanical differences — quoting and parameter spelling — reused from the
    /// replication side rather than restated here.</summary>
    public abstract SqlDialect Sql { get; }

    public abstract DbConnection CreateConnection(string connectionString);

    /// <summary>
    /// How a parameter is written into statement text. Every store writes <c>$name</c> and this
    /// rewrites it, so the SQL in those files stays one readable string rather than an interpolation
    /// of dialect calls.
    /// </summary>
    public string Parameter(string name) => Sql.ParameterReference(name);

    /// <summary>The name a parameter is *bound* under, which is not always how it is written — SQLite
    /// accepts the sigil, and the other two providers want it stripped or prefixed their own way.</summary>
    public abstract string ParameterName(string name);

    // ---- Structural differences ----

    /// <summary>
    /// Caps a result set. Written as a suffix so the caller's query reads in its natural order.
    /// <para>
    /// SQL Server has no <c>LIMIT</c>, and its <c>OFFSET/FETCH</c> requires an <c>ORDER BY</c> — every
    /// limited query in this project has one, which is not a coincidence: an unordered "first N" is a
    /// question without an answer.
    /// </para>
    /// </summary>
    public abstract string Limit(string parameterName);

    /// <summary>
    /// An insert that does nothing when it would violate <paramref name="conflictTarget"/>.
    /// <para>
    /// The idiom the queue, the lock table and the log's journal-replay guard all depend on for
    /// idempotency, so it has to mean exactly the same thing on all three engines: the row is either
    /// inserted or already there, never an error, and never a second row.
    /// </para>
    /// </summary>
    /// <param name="conflictTarget">
    /// The columns whose uniqueness is being relied on, or null for "whatever unique constraint this
    /// hits". Null is only correct where exactly one such constraint exists on the table.
    /// </param>
    /// <param name="conflictWhere">
    /// The predicate of a **partial** unique index, when that is what is being relied on. The work
    /// queue's uniqueness is exactly this shape — one in-flight row per mapping, where "in-flight" is
    /// a set of statuses — and getting it wrong is not a syntax error but a silently wrong answer:
    /// without the predicate, a mapping that has ever run could never be queued again.
    /// </param>
    public abstract string InsertOrIgnore(
        string table, string columns, string values, string? conflictTarget, string? conflictWhere = null);

    /// <summary>
    /// An insert that overwrites the conflicting row's <paramref name="updates"/> instead.
    /// </summary>
    /// <param name="updates">
    /// Assignments in <c>Column = EXCLUDED.Column</c> form, with the incoming row spelled
    /// <c>EXCLUDED</c> — rewritten per engine. SQL Server's MERGE calls it <c>source</c>.
    /// </param>
    public abstract string Upsert(
        string table, string columns, string values, string conflictTarget, string updates);

    // ---- Schema ----

    /// <summary>An auto-assigned integer primary key column.</summary>
    public abstract string IdentityKey(string column);

    /// <summary>
    /// Free text of any length — a log message, an error summary, a note.
    /// <para>
    /// Distinct from <see cref="KeyText"/> for one reason, and it is SQL Server's: an index cannot
    /// cover an <c>NVARCHAR(MAX)</c> column. Any column this schema indexes or keys has to be bounded
    /// there, so the schema has to say which columns those are. SQLite and Postgres do not care and
    /// get <c>TEXT</c> for both.
    /// </para>
    /// </summary>
    public abstract string Text { get; }

    /// <summary>Text that participates in a primary key, a foreign key or an index. Bounded on SQL
    /// Server — see <see cref="Text"/>.</summary>
    public abstract string KeyText { get; }

    public abstract string Integer { get; }

    /// <summary>
    /// How a column is added to an existing table. SQLite and Postgres write <c>ADD COLUMN</c>; SQL
    /// Server writes <c>ADD</c> and rejects the keyword. One word, and it is the whole of the
    /// difference between the three engines' <c>ALTER TABLE</c>.
    /// </summary>
    public virtual string AddColumn => "ADD COLUMN";

    /// <summary>
    /// The applied-migration count, and how it is recorded.
    /// <para>
    /// SQLite has <c>PRAGMA user_version</c>, a single integer that costs nothing to read on every
    /// connection. The other two need a table, which is the same idea written down.
    /// </para>
    /// </summary>
    public abstract int GetSchemaVersion(DbConnection connection);

    public abstract void SetSchemaVersion(DbConnection connection, int version);

    /// <summary>Runs once on every freshly opened connection, before anything else.</summary>
    public virtual void OnConnectionOpened(DbConnection connection) { }

    /// <summary>
    /// Whether a failed statement is worth trying again.
    /// <para>
    /// **False everywhere but SQLite**, and that is the finding rather than an omission. SQLite's
    /// retry loop exists because one writer holds a lock on a *file* and everybody else is told to go
    /// away; a client-server engine queues that contention internally and the caller simply waits. See
    /// <see cref="SqliteStateDialect"/> for the one place this returns true.
    /// </para>
    /// </summary>
    public virtual bool ShouldRetry(Exception exception) => false;
}
