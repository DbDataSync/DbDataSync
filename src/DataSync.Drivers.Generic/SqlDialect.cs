using System.Data.Common;

namespace DataSync.Drivers.Generic;

/// <summary>
/// The small, mechanical ways SQL engines disagree — quoting, parameter placeholders, switching the
/// current database — so that a statement whose *shape* is identical everywhere does not need one
/// copy per engine.
/// <para>
/// This is deliberately not an attempt to abstract over engines. Anything that differs structurally —
/// bulk loading, upsert syntax, identity handling, catalog queries — belongs in an engine-specific
/// implementation with a prefixed Kind. If generalising something here would need a flag per engine,
/// that is the signal it does not belong here.
/// </para>
/// </summary>
public abstract class SqlDialect
{
    /// <summary>Quotes a table, schema or column name. Identifiers reach this from introspected
    /// metadata or validated config, never raw end-user input (architecture/detailed-design.md §6);
    /// implementations still escape the closing delimiter so a name containing one cannot break out.</summary>
    public abstract string QuoteIdentifier(string identifier);

    /// <summary>How a parameter is referenced *in statement text*: <c>@p</c> on SQL Server and MySQL,
    /// <c>:p</c> on Oracle.</summary>
    public abstract string ParameterReference(string name);

    /// <summary>What <see cref="DbParameter.ParameterName"/> must be set to for the same parameter.
    /// Usually identical to <see cref="ParameterReference"/>; some providers want the bare name
    /// without its sigil, which is why the two are separate.</summary>
    public virtual string ParameterName(string name) => ParameterReference(name);

    public virtual string QualifyTable(string schema, string table) =>
        string.IsNullOrEmpty(schema) ? QuoteIdentifier(table) : $"{QuoteIdentifier(schema)}.{QuoteIdentifier(table)}";

    /// <summary>
    /// Points an open connection at <paramref name="database"/>. Virtual because "database" is not
    /// universal: engines where a connection cannot change database (Oracle, where the schema is the
    /// unit) override this to validate-and-ignore rather than call
    /// <see cref="DbConnection.ChangeDatabase"/>, which they throw from.
    /// </summary>
    public virtual Task UseDatabaseAsync(DbConnection connection, string database, CancellationToken cancellationToken)
    {
        connection.ChangeDatabase(database);
        return Task.CompletedTask;
    }
}
