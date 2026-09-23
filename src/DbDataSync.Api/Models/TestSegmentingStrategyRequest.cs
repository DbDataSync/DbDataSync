using DbDataSync.Core.Config;

namespace DbDataSync.Api.Models;

/// <summary>
/// The editor's Test button's request body — a strategy that has not been saved, plus the column to
/// run it over.
/// <para>
/// A separate field rather than one on <see cref="SegmentingStrategyConfig"/> itself, because the
/// column is never part of the strategy — see that type's own doc comment. The editor collects it as
/// scratch state for the test only; nothing here is persisted onto the strategy being edited.
/// </para>
/// </summary>
public sealed record TestSegmentingStrategyRequest(SegmentingStrategyConfig Strategy, string? Column);
