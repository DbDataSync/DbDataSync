using DbDataSync.Core.Config;
using DbDataSync.Drivers.Abstractions;

namespace DbDataSync.Scripting.Abstractions;

/// <summary>Which side of a mapping a verification query is being built for.</summary>
public enum VerificationSideKind
{
    Source,
    Target,
}

/// <summary>
/// Generates a verification query rather than executing a fixed one — see phase 43.
/// <para>
/// A peer of <see cref="ISourceQueryBuilder"/>, and for the same reason: some checks cannot be
/// written down in advance because what to ask depends on what the source's own catalog says. The
/// script returns a statement and the host runs it, so parameter binding stays where phases 9, 17, 22
/// and 26 put it even though the SQL came from someone's C#.
/// </para>
/// <para>
/// Called **once per side**, so a builder can produce genuinely different SQL for two engines — which
/// is the case a per-dialect hand-written check covers and the case a generic one does not.
/// </para>
/// </summary>
public interface IVerificationQueryBuilder
{
    SourceQuery BuildQuery(VerificationQueryContext context);

    /// <summary>
    /// Which result columns identify a row and which are the numbers.
    /// <para>
    /// Declared by the script rather than by the check that binds it: a generated query decides its
    /// own shape, and a config restating it would be a second place to keep in step with the SQL.
    /// Both sides must agree, or there is nothing to line up.
    /// </para>
    /// </summary>
    VerificationQueryShape DescribeResult(VerificationQueryContext context);
}

/// <param name="Side">Which side this call is for. The one thing that differs between the two calls.</param>
/// <param name="Table">The table on this side, already resolved.</param>
/// <param name="Columns">This side's columns, as its catalog reports them — the metadata a generated
/// check exists to be able to look at.</param>
/// <param name="Filter">The check's filter, if it has one, so a builder can honour it rather than
/// having it bolted on afterwards.</param>
public sealed record VerificationQueryContext(
    VerificationSideKind Side,
    TableRef Table,
    IReadOnlyList<ColumnMapping> ColumnMappings,
    IReadOnlyList<ColumnMetadata> Columns,
    string? Filter,
    IScriptDialect Dialect,
    ScriptParameters Parameters);

/// <param name="GroupColumns">Result columns that identify a row, in the order they are compared.</param>
/// <param name="MeasureColumns">Result columns holding the numbers.</param>
public sealed record VerificationQueryShape(
    IReadOnlyList<string> GroupColumns,
    IReadOnlyList<string> MeasureColumns);
