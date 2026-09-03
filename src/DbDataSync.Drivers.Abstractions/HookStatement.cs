namespace DbDataSync.Drivers.Abstractions;

/// <summary>
/// One statement a lifecycle hook wants run, with its parameters — never a finished string with
/// values interpolated into it. Lives here (rather than in <c>DbDataSync.Core.Config</c>, beside
/// <c>HookConfig</c>) so <c>DbDataSync.Scripting.Abstractions</c>'s <c>ILifecycleHook</c> (phase 27) can
/// reference it without that project taking a dependency on <c>DbDataSync.Drivers.Generic</c>, which is
/// where the config-driven renderer that also produces one of these lives.
/// </summary>
/// <param name="Name">The bare name, as written in the hook body (<c>@rowsWritten</c> is written as
/// <c>"rowsWritten"</c> here) — rendering applies whatever sigil the target dialect uses.</param>
/// <param name="Value">The value to bind. <c>DBNull.Value</c> for a fact that is null (segment,
/// rowsStaged, rowsWritten, watermark can each legitimately be unknown), never a bare CLR
/// <see langword="null"/>.</param>
public sealed record HookParameter(string Name, object Value);

/// <param name="CommandText">Fully substituted and ready to set as <c>DbCommand.CommandText</c> — every
/// token already replaced with a quoted identifier, every parameter reference already rendered in the
/// target dialect's own placeholder syntax.</param>
/// <param name="Parameters">Only the parameters this statement actually references — an unreferenced
/// one is a portability hazard across providers and there is no reason to send it.</param>
public sealed record HookStatement(string CommandText, IReadOnlyList<HookParameter> Parameters);
