using DataSync.Core.Sql;

namespace DataSync.Core.Config;

/// <summary>
/// Stable identifier for a source table within a replication, used as the "SourceTable" key in
/// DataSync.State's ChangeWatermarks table.
/// <para>
/// In Core rather than in the TaskRunner because the runner is no longer the only thing that needs
/// it: the preview (phase 37) reads the stored watermark so that what it describes is the statement
/// the *next* pass would issue. Two spellings of this key would be two answers to "where did this
/// replication get to", which is the one question it exists to answer.
/// </para>
/// </summary>
public static class WatermarkKey
{
    /// <summary>
    /// Built through the source's own dialect rather than by interpolating dots, so the schema and
    /// table cannot be misread as each other.
    /// <para>
    /// The format this replaced keyed schema <c>a.b</c> table <c>c</c> identically to schema <c>a</c>
    /// table <c>b.c</c> — both legal identifiers, one string. A quoted identifier cannot contain its
    /// own closing quote unescaped (see <see cref="SqlDialect.SplitQualifiedName"/>, which states the
    /// same rule from the parsing side), so the two are now distinct strings by construction rather
    /// than by anyone agreeing not to name a schema that way.
    /// </para>
    /// <para>
    /// The database is quoted for the same reason and at the same cost: it is a SQL identifier too,
    /// and <c>conn</c>/<c>db/x</c> would otherwise key the same as <c>conn/db</c>/<c>x</c>. The
    /// connection name is left bare — it is a DataSync config name, not an identifier in any engine,
    /// and there is no dialect that could meaningfully quote it.
    /// </para>
    /// </summary>
    public static string Build(SourceTableRef source, SqlDialect dialect) =>
        $"{source.ConnectionName}/{dialect.QuoteIdentifier(source.Database)}/" +
        $"{dialect.QualifyTable(source.Schema, source.Table)}";
}
