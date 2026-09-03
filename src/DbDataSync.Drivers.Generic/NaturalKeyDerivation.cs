using DbDataSync.Core.Config;
using DbDataSync.Drivers.Abstractions;

namespace DbDataSync.Drivers.Generic;

/// <summary>
/// Which target columns identify a row across its versions, derived from the source's primary key —
/// the SCD Type 2 writer's <c>naturalKey</c> when nobody has stated one.
/// <para>
/// Beside <see cref="ProvisioningColumnBuilder"/>, which does the analogous job for a different
/// question: translate the source's introspected <see cref="ColumnMetadata"/> through the mapping's
/// <see cref="ColumnMapping"/> list into something expressed in the target's names. And shared for the
/// same reason — <c>DbDataSync.Api.Services.ProvisioningService</c> previews what would be derived and
/// <c>DbDataSync.TaskRunner.RunExecutor</c> derives it at run time, and a preview computed by a second
/// code path is a preview that can quietly disagree with what runs.
/// </para>
/// <para>
/// **Primary key only.** A unique index or constraint would be an equally valid business key, and
/// nothing in this codebase introspects one on either driver — <see cref="ColumnMetadata"/> has
/// <see cref="ColumnMetadata.IsPrimaryKey"/> and nothing else to go on. Phase 68 leaves that out
/// deliberately rather than half-inferring it.
/// </para>
/// <para>
/// This reads the *source's* key, not the target's, and that is the whole point: an SCD2 target's own
/// primary key is the surrogate this writer generates, so there is nothing there to read a business
/// key from. What identifies a customer is what identified it at the source.
/// </para>
/// </summary>
public static class NaturalKeyDerivation
{
    /// <summary>
    /// The target-column names of the source's primary key, in the order the source lists its columns
    /// (<see cref="ColumnMetadata"/> carries no key ordinal, and the writer matches on a set anyway).
    /// <para>
    /// Empty in two cases, and neither is an error here: the source has no primary key, or it has one
    /// whose columns this mapping does not write. The second matters — a key column the mapping does
    /// not carry across cannot be matched on at the target, and deriving a partial key would silently
    /// version rows against the wrong identity. Empty means "nothing to derive", and the caller decides
    /// what that means: <c>Scd2Writer.ApplyAsync</c>'s own required-option check already fails a run
    /// with a message that says what is missing.
    /// </para>
    /// </summary>
    public static IReadOnlyList<string> Derive(
        IReadOnlyList<ColumnMetadata> sourceColumns, IReadOnlyList<ColumnMapping> columnMappings)
    {
        var targetOf = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var mapping in columnMappings)
            targetOf.TryAdd(mapping.SourceColumn, mapping.TargetColumn);

        var keys = new List<string>();
        foreach (var column in sourceColumns.Where(c => c.IsPrimaryKey))
        {
            // One unmapped key column and there is no derivation to make: see the summary.
            if (!targetOf.TryGetValue(column.Name, out var target))
                return [];

            keys.Add(target);
        }

        return keys;
    }

    /// <summary>
    /// How <c>naturalKey</c> is spelled in an options bag — comma-separated, the same shape somebody
    /// typing a composite key by hand produces, so a derived value and a stated one are indistinguishable
    /// by the time the writer splits them.
    /// </summary>
    public static string Format(IReadOnlyList<string> keys) => string.Join(", ", keys);
}
