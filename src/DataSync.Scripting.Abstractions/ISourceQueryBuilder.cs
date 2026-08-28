using DataSync.Core.Config;
using DataSync.Drivers.Abstractions;

namespace DataSync.Scripting.Abstractions;

/// <summary>
/// Owns a source read outright: the script supplies the statement, and the host runs it, binds its
/// parameters and decodes its result.
/// <para>
/// Reached through the generic <c>ScriptedQuery</c> reader Kind rather than by hooking an existing
/// reader — so an operator *chooses* it in the pipeline's reader picker, and no other reader's
/// statement is silently reshaped. See phase 30.
/// </para>
/// <para>
/// Most change-tracking mechanisms are, at read time, a <c>SELECT</c> returning rows with an operation
/// and a position (see <c>planning/todo/change-tracking-strategies.md</c>). This is how DataSync
/// consumes one it has no driver for — including on ODBC and JDBC, where the engine behind the
/// connection is not knowable at design time.
/// </para>
/// </summary>
public interface ISourceQueryBuilder
{
    /// <summary>
    /// Fixes the end of this pass's window — <c>SELECT MAX(seq)</c>, <c>pg_current_wal_lsn()</c>,
    /// <c>CHANGE_TRACKING_CURRENT_VERSION()</c>. Return null when the mechanism has no position of its
    /// own, and the host echoes the previous watermark back rather than inventing one.
    /// <para>
    /// **Before** the read, and returning a single scalar, because <c>ReadResult</c>'s own contract says
    /// the new watermark is "computed by the reader up front … not derived from what was actually read".
    /// Taking the highest position seen while streaming would look equivalent and is not: it cannot
    /// bound the window, so rows arriving mid-read would extend it and the stored watermark would claim
    /// ground the pass never covered.
    /// </para>
    /// </summary>
    SourceQuery? BuildWatermarkQuery(SourceQueryContext context);

    /// <summary>The rows to read. Runs after the watermark query, so
    /// <see cref="SourceQueryContext.EndWatermark"/> is available to bound it.</summary>
    SourceQuery BuildReadQuery(SourceQueryContext context);

    /// <summary>How to read the result set back. Called once per pass, before the read.</summary>
    SourceQueryShape DescribeResult(SourceQueryContext context);
}

/// <summary>
/// A statement and its parameters.
/// <para>
/// Text **plus a typed parameter list**, never a finished string with values in it. That keeps
/// parameter binding in the host — where phases 9, 17 and 22 put it — even when the SQL came from an
/// operator, and it makes a builder's output data that a test can assert on.
/// </para>
/// </summary>
public sealed record SourceQuery(string CommandText, IReadOnlyList<ScriptQueryParameter> Parameters)
{
    public static SourceQuery Text(string commandText) => new(commandText, []);
}

public sealed record ScriptQueryParameter(string Name, object? Value);

/// <param name="OperationColumn">
/// The column carrying each row's operation. **Absent means every row is an insert**, which is watermark
/// semantics — the built-in <c>Watermark</c> reader is a degenerate case of this one.
/// </param>
/// <param name="OperationValues">
/// What that column's values mean: <c>'I'/'U'/'D'</c> for a shadow table, <c>1/2/4</c> for SQL Server
/// CDC, <c>'INSERT'/'UPDATE'/'DELETE'</c> for LogMiner. Matched case-insensitively. A value not in the
/// map falls back to <paramref name="DefaultOperation"/>.
/// </param>
/// <param name="ExcludeColumns">
/// The mechanism's own bookkeeping, dropped from the <c>ChangeSchema</c> before staging sees it — so
/// <c>__$operation</c> and <c>seq</c> do not arrive as columns nobody mapped.
/// </param>
public sealed record SourceQueryShape(
    string? OperationColumn = null,
    IReadOnlyDictionary<string, ChangeOperation>? OperationValues = null,
    ChangeOperation DefaultOperation = ChangeOperation.Insert,
    IReadOnlyList<string>? ExcludeColumns = null);

/// <param name="PreviousWatermark">Null on a first pass, exactly as a driver's reader sees it.</param>
/// <param name="EndWatermark">
/// What <see cref="ISourceQueryBuilder.BuildWatermarkQuery"/> returned, so the read can be bounded by
/// it. Null when there is no watermark query, or when building that query.
/// </param>
/// <param name="Segment">
/// The unit of work's segment, if it has one. A builder owns the whole statement, so it also owns
/// whether and how to scope it — the host does not pre-render a predicate a script may not want.
/// </param>
public sealed record SourceQueryContext(
    SourceTableRef Source,
    IReadOnlyList<ColumnMapping> ColumnMappings,
    IReadOnlyList<ColumnMetadata> SourceColumns,
    string? PreviousWatermark,
    string? EndWatermark,
    BatchReloadSegment? Segment,
    IScriptDialect Dialect,
    ScriptParameters Parameters);
