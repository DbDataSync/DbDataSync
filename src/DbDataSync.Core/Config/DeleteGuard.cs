using System.Text.Json;
using System.Text.Json.Serialization;

namespace DbDataSync.Core.Config;

/// <summary>
/// A sanity check a <see cref="KeyReconcileDeleteWriter"/>-shaped writer runs before committing a
/// delete sweep: how much of a segment's rows may be removed before the writer refuses rather than
/// deletes. The same sealed-hierarchy shape <see cref="BatchReloadSegment"/> already establishes —
/// every mode carries only the fields valid for it.
/// <para>
/// Exists because a key-diff sweep's failure mode is silent and total: a source connected to the wrong
/// database, a WHERE filter that stopped matching, or a source table truncated by mistake all look
/// identical to "every row was deleted" from the writer's side, and a reconciling delete has no second
/// chance to notice once it has committed.
/// </para>
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "mode")]
[JsonDerivedType(typeof(NoneDeleteGuard), "none")]
[JsonDerivedType(typeof(RatioDeleteGuard), "ratio")]
public abstract record DeleteGuard;

/// <summary>No check at all — every delete the sweep computes is applied. What an explicit override
/// (the operator confirming "yes, delete this many") asks for.</summary>
public sealed record NoneDeleteGuard : DeleteGuard;

/// <summary>Refuses when the deleted count exceeds <paramref name="MaxRatio"/> of the segment's total
/// row count — <c>0.5</c> by default, so a sweep that would remove more than half of what it scoped
/// stops rather than commits.</summary>
public sealed record RatioDeleteGuard(double MaxRatio = 0.5) : DeleteGuard;

/// <param name="Ok">False means the writer must roll back rather than commit.</param>
/// <param name="Message">Names the observed ratio, the limit, and that an override exists — null when
/// <paramref name="Ok"/> is true.</param>
public sealed record DeleteGuardResult(bool Ok, string? Message)
{
    public static readonly DeleteGuardResult Passed = new(true, null);
}

/// <summary>Pure evaluation, assertable without a server or a database — every writer that runs a
/// guard calls this rather than re-deriving the arithmetic.</summary>
public static class DeleteGuardEvaluator
{
    public static DeleteGuardResult Check(DeleteGuard guard, long scopeCount, long deleted) => guard switch
    {
        NoneDeleteGuard => DeleteGuardResult.Passed,
        RatioDeleteGuard ratio => CheckRatio(ratio, scopeCount, deleted),
        _ => throw new ArgumentOutOfRangeException(nameof(guard), guard, "Unknown delete guard mode."),
    };

    private static DeleteGuardResult CheckRatio(RatioDeleteGuard guard, long scopeCount, long deleted)
    {
        // An empty scope has nothing to protect — a segment that matched no target rows at all is not
        // a runaway delete, whatever the ratio arithmetic would say about 0/0.
        if (scopeCount <= 0)
            return DeleteGuardResult.Passed;

        var ratio = (double)deleted / scopeCount;
        if (ratio <= guard.MaxRatio)
            return DeleteGuardResult.Passed;

        return new DeleteGuardResult(false,
            $"Deleting {deleted} of {scopeCount} row(s) in scope ({ratio:P1}) exceeds this guard's limit " +
            $"of {guard.MaxRatio:P0}. Re-run with an override (a 'none' guard) to delete anyway.");
    }
}

/// <summary>
/// The one place a <see cref="DeleteGuard"/> is serialized into a work item's options bag — the
/// ephemeral <c>options["deleteGuard"]</c> channel, the same shape
/// <see cref="SegmentSerializer.SegmentOptionKey"/> already established for a segment. No YAML form
/// here: that is phase 125's, once <c>ReconcileConfig</c> gives a guard a persisted home.
/// </summary>
public static class DeleteGuardOption
{
    public const string OptionKey = "deleteGuard";

    public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web);

    public static string Serialize(DeleteGuard guard) => JsonSerializer.Serialize<DeleteGuard>(guard, Options);

    public static DeleteGuard Deserialize(string json) =>
        JsonSerializer.Deserialize<DeleteGuard>(json, Options)
        ?? throw new InvalidOperationException("DeleteGuard JSON deserialized to null.");

    /// <summary>The configured guard, or a default <see cref="RatioDeleteGuard"/> (0.5) when the work
    /// item carries none — a delete sweep that forgot to think about a guard still gets one.</summary>
    public static DeleteGuard Read(IReadOnlyDictionary<string, string> options) =>
        options.TryGetValue(OptionKey, out var json) && !string.IsNullOrWhiteSpace(json)
            ? Deserialize(json)
            : new RatioDeleteGuard();
}
