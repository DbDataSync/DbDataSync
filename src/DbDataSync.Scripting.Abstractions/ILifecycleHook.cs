using DbDataSync.Core.Config;
using DbDataSync.Drivers.Abstractions;

namespace DbDataSync.Scripting.Abstractions;

/// <summary>
/// C# that generates the SQL a lifecycle hook point runs — see phase 27. One slot
/// (<see cref="ScriptSlots.LifecycleHook"/>), not four: a script is a type, and four bindings for one
/// concern would be four places to keep in sync.
/// <para>
/// <b>A script returns a description of what to do; the host does it.</b> A hook script never holds a
/// connection, never opens a transaction, and cannot commit — it emits statements and parameters, and
/// the host (<c>DbDataSync.TaskRunner.RunExecutor</c>) binds and executes them, exactly as it does for
/// phase 26's config-driven hooks. Injection safety stays where phases 9, 17, 22 and 26 put it, even
/// though the SQL now came from a loop in someone's C#.
/// </para>
/// </summary>
public interface ILifecycleHook
{
    /// <summary>
    /// Which points this hook wants, declared once per pass before the first read. So the host does
    /// not open a source connection — or call <see cref="BuildStatements"/> at all — for a point the
    /// hook does nothing at. Called with a context whose run-specific facts
    /// (<see cref="HookRunFacts.RowsWritten"/> and friends) are still unknown at this moment: returning
    /// an empty list from <see cref="BuildStatements"/> at a point where nothing should run today is
    /// the portable way to say "not this pass".
    /// </summary>
    IReadOnlyList<string> DeclarePoints(LifecycleHookContext context);

    /// <summary>Statements, with their parameters — never a finished string with values interpolated
    /// into it. An empty list is a no-op, not an error: the portable spelling of "don't run", the same
    /// role phase 26 recorded <c>IF @rowsWritten &gt; 0 …</c> as a workaround for.</summary>
    IReadOnlyList<HookStatement> BuildStatements(string point, LifecycleHookContext context);
}

/// <summary>
/// Everything phase 26's built-in <c>@parameters</c> expose, as typed values a script can branch on.
/// </summary>
/// <param name="Segment">Null for an ordinary incremental pass or a whole-table reload.</param>
/// <param name="RowsStaged">Null before staging has happened this segment.</param>
/// <param name="RowsWritten">Null before the load has happened this segment.</param>
/// <param name="StagingLocation">The staged set's qualified location, or null before it exists.</param>
/// <param name="Watermark">The previous watermark, or null on a full load / a reader with none.</param>
public sealed record HookRunFacts(
    Guid RunId,
    string Replication,
    string Mapping,
    string RunKind,
    string? Segment,
    int SegmentIndex,
    int SegmentCount,
    bool IsLastSegment,
    long? RowsStaged,
    long? RowsWritten,
    string? StagingLocation,
    string? Watermark);

/// <summary>
/// What a lifecycle hook script is told. <see cref="SourceColumns"/> and <see cref="TargetColumns"/> are
/// what make schema evolution writable: the useful hook is "for each mapped source column with no
/// target column, emit <c>ALTER TABLE … ADD</c>", and rendering that column's type is
/// <see cref="IScriptDialect.ToCanonicalType"/> + <see cref="IScriptDialect.RenderColumnType"/>.
/// <para>
/// Two dialects, not one, because the motivating case translates a type *across* engines: a source
/// column's native type has to be parsed by the engine that produced it
/// (<see cref="SourceDialect"/>.<c>ToCanonicalType</c>) before the target engine can render DDL for it
/// (<see cref="TargetDialect"/>.<c>RenderColumnType</c>). The phase 27 doc sketches this as one
/// <c>Dialect</c> field; splitting it is the only way the schema-evolution case it motivates actually
/// works for a mapping whose source and target are different engines.
/// </para>
/// </summary>
public sealed record LifecycleHookContext(
    string Point,
    SourceTableRef Source,
    TableRef Target,
    IReadOnlyList<ColumnMapping> ColumnMappings,
    IReadOnlyList<ColumnMetadata> SourceColumns,
    IReadOnlyList<ColumnMetadata> TargetColumns,
    IScriptDialect SourceDialect,
    IScriptDialect TargetDialect,
    HookRunFacts Run,
    ScriptParameters Parameters,
    Action<string> Log);
