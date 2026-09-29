using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using DbDataSync.Updates;

// Phase 196L. The service and PATH run this; it runs whichever slot current.txt names, in this same process, and
// exits with that slot's exit code. An update installs into the other slot and flips the pointer — nothing here
// ever changes, so nothing the service or PATH points at is ever replaced while it runs.

var root = AppContext.BaseDirectory;

string payload;
string slot;
try
{
    (slot, payload) = Resolve(root);
}
catch (LauncherException ex)
{
    Console.Error.WriteLine($"dbdatasync: {ex.Message}");
    return 1;
}

var payloadDirectory = Path.GetDirectoryName(payload)! + Path.DirectorySeparatorChar;

// Process-global values the payload reads as "where am I". Without these it would see the launcher's directory:
// InstallLocator would misidentify the install, wwwroot and the TaskRunner worker would be looked for beside the
// launcher, and DependencyContext (MVC's application-part discovery) would read the launcher's deps file.
AppContext.SetData("APP_CONTEXT_BASE_DIRECTORY", payloadDirectory);
AppContext.SetData("APP_CONTEXT_DEPS_FILES", Path.ChangeExtension(payload, ".deps.json"));
AppContext.SetData(SlotPaths.RootDataKey, root);
AppContext.SetData(SlotPaths.SlotDataKey, slot);

// The Default context, not a private one: driver plugins (DriverPluginLoadContext) and LibraryRegistry defer shared
// contracts to Default, so the payload has to be there — exactly where it is when it runs as its own app. The
// resolver reads the payload's deps.json, which is what finds runtimes/<rid>/native/ for SQLite, libgit2, DuckDB.
var resolver = new AssemblyDependencyResolver(payload);
AssemblyLoadContext.Default.Resolving += (context, name) =>
    resolver.ResolveAssemblyToPath(name) is { } path ? context.LoadFromAssemblyPath(path) : null;
AssemblyLoadContext.Default.ResolvingUnmanagedDll += (_, name) =>
    resolver.ResolveUnmanagedDllToPath(name) is { } path ? NativeLibrary.Load(path) : IntPtr.Zero;

var assembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(payload);
Assembly.SetEntryAssembly(assembly);

var entryPoint = assembly.EntryPoint
    ?? throw new InvalidOperationException($"{payload} has no entry point.");

try
{
    var result = entryPoint.Invoke(null, entryPoint.GetParameters().Length == 0 ? [] : [args]);
    return result is int code ? code : 0;
}
catch (TargetInvocationException ex) when (ex.InnerException is not null)
{
    // Rethrown as the payload threw it, so an unhandled exception looks exactly as it would without the launcher.
    ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
    throw;
}

static (string Slot, string Payload) Resolve(string root)
{
    var pointer = SlotPaths.PointerPath(root);
    if (!File.Exists(pointer))
        throw new LauncherException(
            $"{pointer} is missing, so there is no version to run. Write \"a\" or \"b\" into it — whichever of " +
            $"{Path.Combine(root, SlotPaths.VersionsDirectoryName)}{Path.DirectorySeparatorChar}a or b holds an install.");

    var slot = SlotPaths.ParseSlot(File.ReadAllText(pointer))
        ?? throw new LauncherException($"{pointer} must contain \"a\" or \"b\", and nothing else.");

    var directory = SlotPaths.SlotDirectory(root, slot);
    var payloads = SlotPaths.FindPayloads(directory);
    return payloads.Count switch
    {
        1 => (slot, payloads[0]),
        0 => throw new LauncherException(
            $"{pointer} names slot {slot}, but {directory} has no DbDataSync install. " +
            $"If the other slot has one, write \"{SlotPaths.Other(slot)}\" into {pointer}."),
        _ => throw new LauncherException(
            $"{directory} holds more than one DbDataSync install ({string.Join(", ", SlotPaths.InstalledVersions(directory))}), " +
            "so which one to run is ambiguous. Remove all but one with `dotnet tool uninstall --tool-path " + directory + " DbDataSync` " +
            "and install the one wanted."),
    };
}

internal sealed class LauncherException(string message) : Exception(message);
