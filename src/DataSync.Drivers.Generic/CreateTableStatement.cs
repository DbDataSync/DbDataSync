using DataSync.Drivers.Abstractions;

namespace DataSync.Drivers.Generic;

/// <summary>One column a generated <c>CREATE TABLE</c> should carry, already translated into its
/// *target* dialect's rendering — see <see cref="SqlDialect.RenderColumnType"/>. Identity/generated
/// columns are never represented here at all: <see cref="ProvisioningColumn.IsPrimaryKey"/> aside,
/// dropping <c>IsIdentity</c> on the way in (with a warning) is the caller's job, not this renderer's —
/// see phase 25 §4.</summary>
public sealed record CreateTableColumn(string Name, RenderedColumnType Type, bool IsNullable, bool IsPrimaryKey);

/// <summary>
/// Renders a <c>CREATE TABLE</c> for a table that does not exist yet, from already-translated column
/// types. Beside <see cref="StagingStatement.BuildCreate"/> — the existing precedent for "a CREATE
/// TABLE every driver shares" — and deliberately not a copy of it: staging's version takes the
/// *target*'s own native type strings verbatim (there is no other engine in play), while this one takes
/// a canonical rendering because the source and target here are, in general, two different engines.
/// </summary>
public static class CreateTableStatement
{
    public static string Build(SqlDialect dialect, string qualifiedTable, IReadOnlyList<CreateTableColumn> columns)
    {
        if (columns.Count == 0)
            throw new ArgumentException("A CREATE TABLE needs at least one column.", nameof(columns));

        var defs = columns.Select(c =>
            $"{dialect.QuoteIdentifier(c.Name)} {c.Type.Sql} {(c.IsNullable ? "NULL" : "NOT NULL")}");

        var primaryKey = columns.Where(c => c.IsPrimaryKey).Select(c => dialect.QuoteIdentifier(c.Name)).ToList();
        var primaryKeyClause = primaryKey.Count == 0 ? "" : $",\n    PRIMARY KEY ({string.Join(", ", primaryKey)})";

        // One column per line. A forty-column table on one line is unreadable wherever it is shown,
        // and formatting it here rather than in whatever displays it means every reader of this
        // statement — the Setup card, a copied-out script, a log line — gets the same thing.
        return $"CREATE TABLE {qualifiedTable} (\n    {string.Join(",\n    ", defs)}{primaryKeyClause}\n);";
    }
}
