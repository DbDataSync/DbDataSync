using DbDataSync.Drivers.Abstractions;
using Xunit;

namespace DbDataSync.Drivers.Abstractions.Tests;

public sealed class ChangeSchemaTests
{
    private static readonly ChangeSchema Schema = new(["Id", "Region", "Amount"]);

    [Fact]
    public void Ordinals_FollowTheDeclaredColumnOrder()
    {
        Assert.Equal(0, Schema.GetOrdinal("Id"));
        Assert.Equal(1, Schema.GetOrdinal("Region"));
        Assert.Equal(2, Schema.GetOrdinal("Amount"));
        Assert.Equal(3, Schema.Count);
    }

    /// <summary>SQL Server resolves column names case-insensitively, and the rest of this layer
    /// compares them the same way; a mapping written as "id" must find "Id".</summary>
    [Fact]
    public void ColumnLookup_IsCaseInsensitive()
    {
        Assert.Equal(0, Schema.GetOrdinal("id"));
        Assert.True(Schema.TryGetOrdinal("REGION", out var ordinal));
        Assert.Equal(1, ordinal);
    }

    /// <summary>A mapping naming a column the reader doesn't produce used to be a silent miss that
    /// wrote NULL into the target. It says so now, and names what was actually available.</summary>
    [Fact]
    public void UnknownColumn_ThrowsAndListsWhatWasAvailable()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => Schema.GetOrdinal("Nope"));

        Assert.Contains("'Nope'", ex.Message);
        Assert.Contains("Id, Region, Amount", ex.Message);
    }

    [Fact]
    public void TryGetOrdinal_ReportsAnUnknownColumnWithoutThrowing()
    {
        Assert.False(Schema.TryGetOrdinal("Nope", out _));
    }

    [Fact]
    public void Row_ReadsByOrdinalAndByName()
    {
        var row = new ChangeRow(ChangeOperation.Update, Schema, [7, "EU", 12.5m]);

        Assert.Equal(7, row[0]);
        Assert.Equal("EU", row["Region"]);
        Assert.Equal(12.5m, row["Amount"]);
        Assert.Equal(ChangeOperation.Update, row.Operation);
    }

    /// <summary>A delete carries only its key; the remaining slots exist but were never populated.
    /// Writers distinguish the cases by <see cref="ChangeRow.Operation"/>, not by presence.</summary>
    [Fact]
    public void DeleteRow_HasKeyOnly_WithTheRestNull()
    {
        var row = new ChangeRow(ChangeOperation.Delete, Schema, [7, null, null]);

        Assert.Equal(7, row["Id"]);
        Assert.Null(row["Region"]);
        Assert.Null(row["Amount"]);
    }

    /// <summary>Every row in a read shares one schema instance — that is the whole point of hoisting
    /// the names out of the rows, so it is worth pinning.</summary>
    [Fact]
    public void RowsShareTheSchemaInstance()
    {
        var first = new ChangeRow(ChangeOperation.Insert, Schema, [1, "EU", 1m]);
        var second = new ChangeRow(ChangeOperation.Insert, Schema, [2, "US", 2m]);

        Assert.Same(first.Schema, second.Schema);
    }
}
