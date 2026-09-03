using DbDataSync.Core.Config;
using DbDataSync.Drivers.Abstractions;

namespace DbDataSync.Scripting.Abstractions;

/// <summary>
/// Transforms a whole row in this process as it flows from the reader to staging, and may drop it.
/// The most powerful of the three transform slots and the most expensive.
/// </summary>
public interface IRowTransform
{
    /// <summary>
    /// Called **once per pass**, before any row, so a transform that changes the row's shape can say so.
    /// Return <paramref name="input"/> unchanged when the shape is unchanged.
    /// <para>
    /// Easy to leave out and expensive to add later: staging builds its table from the schema of the
    /// first row it sees, so a column this adds has to be knowable *before* that row — otherwise the
    /// staging table is built from the untransformed shape and every added column is silently dropped.
    /// </para>
    /// </summary>
    ChangeSchema DeclareSchema(ChangeSchema input, RowTransformContext context);

    /// <summary>Return null to drop the row. Deletes reach here too — <c>row.Operation</c> is on the
    /// row, and "drop the deletes" is a legitimate thing to want.</summary>
    ValueTask<ChangeRow?> TransformAsync(ChangeRow row, RowTransformContext context, CancellationToken cancellationToken);
}

/// <param name="Log">Writes into the run's log stream, so a script's own output lands in the Runs tab
/// where whoever is debugging it is already looking.</param>
public sealed record RowTransformContext(
    IReadOnlyList<ColumnMapping> ColumnMappings,
    IScriptDialect Dialect,
    ScriptParameters Parameters,
    Action<string> Log);
