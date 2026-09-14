using DbDataSync.Libraries.CompatFixtureLibrary;

namespace DbDataSync.Libraries.CompatFixtureDriver;

/// <summary>Everything a real driver's own reader/staging-provider/writer code would do to its
/// required library — a handful of concrete member touches, compiled for real against
/// DbDataSync.Libraries.CompatFixtureLibraryComplete. LibrarySurfaceExtractor reads this method's own
/// IL, not its behavior; it is never executed by the test.</summary>
public static class DriverUsage
{
    public static string UseLibrary()
    {
        var widget = new Widget { Name = "hello" };
        widget.DoThing();
        widget.DoThing(5);
        return widget.Name;
    }
}
