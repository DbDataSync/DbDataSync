namespace DbDataSync.Libraries.CompatFixtureLibrary;

/// <summary>The fake "library" a fake "driver" (<c>DbDataSync.Libraries.CompatFixtureDriver</c>) uses a
/// few members of — the complete version, carrying every member the driver actually calls. Its sibling
/// project, <c>DbDataSync.Libraries.CompatFixtureLibraryIncomplete</c>, is identical except for the one
/// deliberately-removed overload (<see cref="DoThing(int)"/>) the unit test proves gets named as
/// missing.</summary>
public sealed class Widget
{
    public Widget() { }

    public string Name = "";

    public void DoThing() { }

    public void DoThing(int times) { }
}
