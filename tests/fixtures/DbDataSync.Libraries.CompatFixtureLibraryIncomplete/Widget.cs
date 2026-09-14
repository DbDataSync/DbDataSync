namespace DbDataSync.Libraries.CompatFixtureLibrary;

/// <summary>The "incomplete" version: identical to
/// DbDataSync.Libraries.CompatFixtureLibraryComplete's <c>Widget</c> except <c>DoThing(int)</c> is
/// gone — the one used member the unit test expects <c>LibrarySurfaceChecker.Check</c> to name as
/// missing. <c>DoThing()</c> (no args), the <c>Name</c> field and the default constructor are all still
/// here, and must NOT be reported missing — proving the check isn't just failing everything about this
/// type.</summary>
public sealed class Widget
{
    public Widget() { }

    public string Name = "";

    public void DoThing() { }
}
