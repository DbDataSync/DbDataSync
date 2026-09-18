namespace DbDataSync.Drivers.Abstractions;

/// <summary>
/// Column names a reader appends to its own <see cref="ChangeSchema"/>, beyond the mapped business
/// columns, when it can state the true source order and time of each individual change — phase 132.
/// A staging provider that finds both by name in a row's schema persists them as real staging columns
/// and reports <see cref="StagedChangeSet.HasChangeOrdering"/>; a writer that finds that flag set can
/// process a key with more than one staged row correctly instead of colliding.
/// </summary>
public static class ChangeOrdering
{
    /// <summary>Sortable as text, unique per row, in true source order — CDC's
    /// <c>(__$start_lsn, __$seqval)</c> rendered as fixed-width hex, so two rows never share one even
    /// within the same transaction.</summary>
    public const string OrderingColumn = "__DS_ChangeOrdering";

    /// <summary>The wall-clock time the engine associates with this row's change — CDC's
    /// <c>sys.fn_cdc_map_lsn_to_time(__$start_lsn)</c>. Best-effort: the function interpolates between
    /// points the capture job recorded, so two rows in the same transaction share this value even
    /// though <see cref="OrderingColumn"/> tells them apart. Never the pass time.</summary>
    public const string ChangedAtColumn = "__DS_ChangedAtUtc";

    /// <summary>
    /// Whether a schema carries both columns — the test <see cref="Abstractions.IStagingProvider"/>
    /// implementations use to decide whether to persist them.
    /// </summary>
    public static bool IsPresent(ChangeSchema schema) =>
        schema.TryGetOrdinal(OrderingColumn, out _) && schema.TryGetOrdinal(ChangedAtColumn, out _);

    /// <summary>
    /// Peeks the first row's schema without losing it, so a staging provider can decide its own DDL —
    /// whether to add the two ordering columns — before it has read a single row for real.
    /// <para>
    /// Every staging provider in this codebase creates its staging table ahead of reading any row, so
    /// "does this pass's schema carry the ordering columns" cannot wait until enumeration is done — it
    /// has to be answered from the very first row, with that row still delivered, in order, to whatever
    /// reads the sequence afterwards. This is the one place that peek-and-replay happens, so every
    /// staging provider does it identically rather than each inventing its own.
    /// </para>
    /// <para>
    /// An empty sequence answers <c>false</c> — there is no row to carry the columns, and nothing will
    /// be staged either way.
    /// </para>
    /// </summary>
    public static async Task<(bool HasChangeOrdering, IAsyncEnumerable<ChangeRow> Rows)> DetectAsync(
        IAsyncEnumerable<ChangeRow> rows, CancellationToken cancellationToken)
    {
        var enumerator = rows.GetAsyncEnumerator(cancellationToken);
        if (!await enumerator.MoveNextAsync())
        {
            await enumerator.DisposeAsync();
            return (false, Empty());
        }

        var first = enumerator.Current;
        return (IsPresent(first.Schema), Replay(first, enumerator));
    }

    /// <summary>
    /// The peeked row and then the rest, with the inner enumerator disposed however this ends.
    /// <para>
    /// <c>yield return first</c> is inside the <c>try</c>, and that is load-bearing rather than tidy.
    /// An iterator suspended at a <c>yield</c> outside its <c>try</c> runs no <c>finally</c> when it is
    /// disposed — so with the first yield outside, a consumer that threw while handling the *first*
    /// staged row left the source reader open, and the next thing to use that connection failed with
    /// "a command is already in progress" rather than with whatever actually went wrong. Found by phase
    /// 38's own "a value binary COPY cannot convert fails legibly" test, whose bad value is in the
    /// first row and which then staged the same rows through the other provider.
    /// </para>
    /// </summary>
    private static async IAsyncEnumerable<ChangeRow> Replay(ChangeRow first, IAsyncEnumerator<ChangeRow> rest)
    {
        try
        {
            yield return first;
            while (await rest.MoveNextAsync())
                yield return rest.Current;
        }
        finally
        {
            await rest.DisposeAsync();
        }
    }

    private static async IAsyncEnumerable<ChangeRow> Empty()
    {
        await Task.CompletedTask;
        yield break;
    }
}
