using DbDataSync.Api.Services;
using DbDataSync.Core.Config;
using Xunit;

namespace DbDataSync.Api.Tests;

/// <summary>
/// The auto-map rule, pinned on its own.
/// <para>
/// **This is the C# half of a rule that also exists in TypeScript** — <c>autoMap</c> in
/// <c>ColumnMappingEditor.tsx</c>, the "Auto-map by name" button. A mapping created in bulk is never
/// opened in that editor and has to arrive with the columns pressing that button would have produced,
/// so the two have to agree; they cannot share code across the language boundary, and these cases are
/// what keeps them honest. Each one below is a case the editor answers, written here as the same
/// question.
/// </para>
/// </summary>
public sealed class ColumnAutoMapTests
{
    private static CachedColumn Col(string name) => new(name, "int", false, false, false);

    private static IReadOnlyList<CachedColumn> Cols(params string[] names) => [.. names.Select(Col)];

    [Fact]
    public void TwoIdenticalTables_MapOneToOne()
    {
        var mapped = ColumnAutoMap.Between(Cols("Id", "Name"), Cols("Id", "Name"));

        Assert.Equal(["Id", "Name"], mapped.Select(m => m.SourceColumn));
        Assert.Equal(["Id", "Name"], mapped.Select(m => m.TargetColumn));
    }

    /// <summary>
    /// The editor filters the *target's* columns by whether the source has them, so a target column
    /// with no source counterpart is left unmapped rather than being fed from nothing.
    /// </summary>
    [Fact]
    public void ATargetColumnTheSourceDoesNotHave_IsLeftUnmapped()
    {
        var mapped = ColumnAutoMap.Between(Cols("Id"), Cols("Id", "LoadedAtUtc"));

        Assert.Equal(["Id"], mapped.Select(m => m.TargetColumn));
    }

    /// <summary>The other way round is the same rule seen from the other side: a source column the
    /// target has nowhere to put is not mapped either.</summary>
    [Fact]
    public void ASourceColumnTheTargetDoesNotHave_IsLeftUnmapped()
    {
        var mapped = ColumnAutoMap.Between(Cols("Id", "Internal"), Cols("Id"));

        Assert.Equal(["Id"], mapped.Select(m => m.SourceColumn));
    }

    /// <summary>
    /// **A target that is not there yet takes the source's columns.** Not a fallback — the editor is
    /// explicit that a not-yet-created target's columns *are* the source's, because
    /// <c>ProvisioningService</c> builds its <c>CREATE TABLE</c> from the column mappings. What is
    /// mapped is what gets created, so mapping nothing would create nothing.
    /// </summary>
    [Fact]
    public void ATargetThatIsNotThere_MapsEverySourceColumn()
    {
        Assert.Equal(["Id", "Name"], ColumnAutoMap.Between(Cols("Id", "Name"), null).Select(m => m.TargetColumn));
    }

    /// <summary>An empty answer from a catalog that itself succeeded means the table is not there —
    /// no engine has a table with no columns — so it is the same case as the one above.</summary>
    [Fact]
    public void ATargetWhoseCatalogAnsweredEmpty_IsTreatedAsNotThere()
    {
        Assert.Equal(["Id"], ColumnAutoMap.Between(Cols("Id"), []).Select(m => m.TargetColumn));
    }

    [Fact]
    public void NoSource_MapsNothing()
    {
        Assert.Empty(ColumnAutoMap.Between(null, Cols("Id", "Name")));
        Assert.Empty(ColumnAutoMap.Between([], Cols("Id", "Name")));
    }

    /// <summary>
    /// Ordinally, as the editor's own Set of source names is. Two columns differing only in case are
    /// two columns to every engine this runs against, and pairing them would write a value into a
    /// column nobody chose.
    /// </summary>
    [Fact]
    public void NamesArePairedCaseSensitively()
    {
        Assert.Empty(ColumnAutoMap.Between(Cols("id"), Cols("ID")));
    }

    /// <summary>The target's order, not the source's — it is the target's columns being walked. The
    /// editor lists them in that order too, so a mapping created in bulk and one created by hand read
    /// the same way down the page.</summary>
    [Fact]
    public void TheTargetsOrderIsKept()
    {
        var mapped = ColumnAutoMap.Between(Cols("A", "B"), Cols("B", "A"));

        Assert.Equal(["B", "A"], mapped.Select(m => m.TargetColumn));
    }

    [Fact]
    public void NothingIsInventedBeyondTheNames()
    {
        var mapped = ColumnAutoMap.Between(Cols("Id"), Cols("Id"));

        var only = Assert.Single(mapped);
        Assert.Null(only.Transform);
        Assert.Null(only.TargetType);
        Assert.Empty(only.Renames);
    }
}
