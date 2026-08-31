using DataSync.Drivers.Abstractions;
using DataSync.Core.Sql;

namespace DataSync.Scripting.Abstractions;

/// <summary>
/// What a script is told about the engine it is generating SQL for.
/// <para>
/// Deliberately narrower than the driver layer's own <c>SqlDialect</c>, and deliberately not that type:
/// the dialect lives in <c>DataSync.Drivers.Generic</c>, which references this project, so taking it
/// here would be circular. It is also a moving target — it gained four hooks across three phases — and
/// a script written today should still compile when it gains a fifth.
/// </para>
/// </summary>
public interface IScriptDialect
{
    /// <summary>The engine's name, for a script that has to branch: <c>MsSql</c>, <c>Postgres</c>.
    /// Matches <c>ConnectionDriverType</c>'s member names.</summary>
    string EngineName { get; }

    string QuoteIdentifier(string identifier);

    /// <summary>How a parameter is written in statement text — <c>@p</c>, <c>:p</c>.</summary>
    string ParameterReference(string name);

    /// <summary>Translates one of this engine's native type specs into the canonical intermediate —
    /// see phase 25. Added for phase 27's <see cref="ILifecycleHook"/>: a schema-evolution hook has to
    /// reason about a column's type across engines, and hand-writing a type map inside a hook is the
    /// worst possible place for one.</summary>
    CanonicalType ToCanonicalType(string nativeType);

    /// <summary>The reverse direction — DDL for a canonical type, in this dialect.</summary>
    RenderedColumnType RenderColumnType(CanonicalType type);
}
