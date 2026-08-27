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
}
