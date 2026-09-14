using DbDataSync.Drivers.Abstractions;
using Xunit;

namespace DbDataSync.Drivers.Abstractions.Tests;

/// <summary>
/// Phase 132's capability signal — <see cref="ChangeOrdering.IsPresent"/> and the peek-and-replay
/// <see cref="ChangeOrdering.DetectAsync"/> a staging provider needs it through, since every staging
/// provider in this codebase creates its table before it has read a single row.
/// </summary>
public sealed class ChangeOrderingTests
{
    private static readonly ChangeSchema WithOrdering =
        new(["Id", "Name", ChangeOrdering.OrderingColumn, ChangeOrdering.ChangedAtColumn]);

    private static readonly ChangeSchema WithoutOrdering = new(["Id", "Name"]);

    [Fact]
    public void IsPresent_TrueOnlyWhenBothColumnsAreThere()
    {
        Assert.True(ChangeOrdering.IsPresent(WithOrdering));
        Assert.False(ChangeOrdering.IsPresent(WithoutOrdering));
    }

    /// <summary>Half a pair is not the pair — a reader that carries only one of the two (which should
    /// never happen, but is not this method's job to assume) does not count.</summary>
    [Fact]
    public void IsPresent_FalseWithOnlyOneOfTheTwoColumns()
    {
        var orderingOnly = new ChangeSchema(["Id", ChangeOrdering.OrderingColumn]);
        var changedAtOnly = new ChangeSchema(["Id", ChangeOrdering.ChangedAtColumn]);

        Assert.False(ChangeOrdering.IsPresent(orderingOnly));
        Assert.False(ChangeOrdering.IsPresent(changedAtOnly));
    }

    private static async IAsyncEnumerable<ChangeRow> Rows(ChangeSchema schema, params object?[][] values)
    {
        foreach (var row in values)
        {
            await Task.Yield();
            yield return new ChangeRow(ChangeOperation.Update, schema, row);
        }
    }

    [Fact]
    public async Task DetectAsync_TrueWhenTheFirstRowsSchemaHasBothColumns()
    {
        var (hasChangeOrdering, _) = await ChangeOrdering.DetectAsync(
            Rows(WithOrdering, [1, "a", "AABB", null]), CancellationToken.None);

        Assert.True(hasChangeOrdering);
    }

    [Fact]
    public async Task DetectAsync_FalseWhenTheSchemaHasNeitherColumn()
    {
        var (hasChangeOrdering, _) = await ChangeOrdering.DetectAsync(
            Rows(WithoutOrdering, [1, "a"]), CancellationToken.None);

        Assert.False(hasChangeOrdering);
    }

    /// <summary>An empty sequence answers false — there is no row to carry the columns, and nothing
    /// will be staged either way — without throwing on the peek.</summary>
    [Fact]
    public async Task DetectAsync_FalseOnAnEmptySequence_AndTheReplayIsStillEmpty()
    {
        var (hasChangeOrdering, replay) = await ChangeOrdering.DetectAsync(Rows(WithOrdering), CancellationToken.None);

        Assert.False(hasChangeOrdering);
        Assert.Empty(await ToListAsync(replay));
    }

    /// <summary>
    /// The whole point of the peek: the row consumed to answer the question is not lost. Every row —
    /// including the first — reaches whatever reads the replayed sequence, in the same order, with the
    /// same values.
    /// </summary>
    [Fact]
    public async Task DetectAsync_ReplaysEveryRowInOrder_IncludingTheOnePeeked()
    {
        var (_, replay) = await ChangeOrdering.DetectAsync(
            Rows(WithOrdering, [1, "a", "AA", null], [2, "b", "BB", null], [3, "c", "CC", null]),
            CancellationToken.None);

        var rows = await ToListAsync(replay);

        Assert.Equal(3, rows.Count);
        Assert.Equal([1, 2, 3], rows.Select(r => (int)r["Id"]!));
        Assert.Equal(["a", "b", "c"], rows.Select(r => (string)r["Name"]!));
    }

    private static async Task<List<ChangeRow>> ToListAsync(IAsyncEnumerable<ChangeRow> rows)
    {
        var list = new List<ChangeRow>();
        await foreach (var row in rows)
            list.Add(row);
        return list;
    }
}
