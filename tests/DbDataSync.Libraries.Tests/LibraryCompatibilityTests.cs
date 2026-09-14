namespace DbDataSync.Libraries.Tests;

/// <summary>
/// Phase 109j's own "how to verify" bar: two fixture assemblies — a fake "driver" referencing a few
/// members of a fake "library," and two versions of that library (one complete, one missing a member
/// the driver uses) — prove the extraction+check mechanism works with no real SqlClient/Npgsql
/// involved. See <c>tests/fixtures/DbDataSync.Libraries.CompatFixtureDriver</c> and its two
/// <c>CompatFixtureLibrary*</c> siblings.
/// </summary>
public sealed class LibraryCompatibilityTests
{
    private const string LibraryAssemblyName = "DbDataSync.Libraries.CompatFixtureLibrary";

    [Fact]
    public void ExtractUsedSurface_FindsEveryMemberTheFixtureDriverActuallyReferences()
    {
        var driverDll = DriverDllPath();

        var used = LibrarySurfaceExtractor.ExtractUsedSurface(driverDll, LibraryAssemblyName);

        Assert.Contains(used, m => m.Kind == UsedMemberKind.Method && m.MemberName == ".ctor" && m.ParameterCount == 0);
        Assert.Contains(used, m => m.Kind == UsedMemberKind.Field && m.MemberName == "Name");
        Assert.Contains(used, m => m.Kind == UsedMemberKind.Method && m.MemberName == "DoThing" && m.ParameterCount == 0);
        Assert.Contains(used, m => m.Kind == UsedMemberKind.Method && m.MemberName == "DoThing" && m.ParameterCount == 1);

        // Nothing outside the target assembly leaked in (System.String, System.Object, etc.).
        Assert.All(used, m => Assert.Equal("DbDataSync.Libraries.CompatFixtureLibrary.Widget", m.DeclaringTypeFullName));
    }

    [Fact]
    public void Check_PassesCleanAgainstTheCompleteLibrary()
    {
        var used = LibrarySurfaceExtractor.ExtractUsedSurface(DriverDllPath(), LibraryAssemblyName);
        var completeDir = FixturePublisher.OutputDirectoryFor("DbDataSync.Libraries.CompatFixtureLibraryComplete");

        var result = LibrarySurfaceChecker.Check(used, completeDir);

        Assert.True(result.Compatible, string.Join("; ", result.MissingMembers));
        Assert.Empty(result.MissingMembers);
    }

    [Fact]
    public void Check_NamesTheSpecificMissingMember_AgainstTheIncompleteLibrary()
    {
        var used = LibrarySurfaceExtractor.ExtractUsedSurface(DriverDllPath(), LibraryAssemblyName);
        var incompleteDir = FixturePublisher.OutputDirectoryFor("DbDataSync.Libraries.CompatFixtureLibraryIncomplete");

        var result = LibrarySurfaceChecker.Check(used, incompleteDir);

        Assert.False(result.Compatible);
        var missing = Assert.Single(result.MissingMembers);
        Assert.Contains("DoThing", missing);
        Assert.Contains("1 arg", missing);

        // The members the incomplete version still has must not be reported.
        Assert.DoesNotContain(result.MissingMembers, m => m.Contains("Name"));
        Assert.DoesNotContain(result.MissingMembers, m => m.Contains("DoThing(0"));
    }

    private static string DriverDllPath() => Path.Combine(
        FixturePublisher.OutputDirectoryFor("DbDataSync.Libraries.CompatFixtureDriver"),
        "DbDataSync.Libraries.CompatFixtureDriver.dll");
}
