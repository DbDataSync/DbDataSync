using System.Diagnostics.CodeAnalysis;
using DbDataSync.Libraries;

namespace DbDataSync.State;

/// <summary>
/// Where a <see cref="StateDialect"/> is looked up by its <see cref="StateEngineIds"/>-style string id
/// — mirrors <c>DbDataSync.Drivers.Abstractions.DriverRegistry</c>'s own shape one layer down.
/// <para>
/// **SQLite registers into <see cref="Default"/> automatically**, not via an explicit call from each
/// composition root — the option the phase doc's own "open questions" section left undecided between a
/// static initialiser and an explicit call. A static initialiser was chosen once it was clear how few
/// places actually construct a <see cref="StateDatabase"/> by engine id (the SQLite-only constructor —
/// the overwhelming majority of call sites, in tests and production alike — never touches this registry
/// at all): threading an explicit registry instance through <see cref="StateDatabase"/>'s constructor
/// and every one of its ~30 callers would have been a much larger diff for the same behaviour, and
/// "works for <c>dotnet test</c> too" (the plan doc's own phrase) is true by construction here rather
/// than needing every test fixture to remember a registration step.
/// </para>
/// <para>
/// **MsSql and Postgres are the one exception, since phase 109g.** Neither can be a stateless static
/// singleton any more — each now needs an injected <see cref="LibraryRegistry"/> to resolve its
/// connection (see <see cref="MsSqlStateDialect"/>'s doc comment) — so they cannot register themselves
/// the moment this class is first touched, the way <see cref="SqliteStateDialect"/> still does.
/// <see cref="RegisterLibraryBackedEngines"/> is the explicit call that takes their place: every
/// <see cref="StateDatabase.FromOptions"/> caller already has (or builds) a <see cref="LibraryRegistry"/>
/// for its own repo root, and makes this call before anything downstream asks
/// <see cref="StateDialect.For"/> for either id. Calling it more than once (a second
/// <see cref="StateDatabase.FromOptions"/> call in the same process, or a test that opens more than one
/// non-SQLite <see cref="StateDatabase"/>) is harmless — <see cref="Register"/> just replaces the same
/// engine id, the same way re-running <see cref="LoadAll"/> would for a driver.
/// </para>
/// <para>
/// A custom dialect (a compiled plugin, 109h) still has a real extension point:
/// <see cref="Default"/>.<see cref="Register"/> is public, so a composition root that has loaded one
/// registers it there before anything asks <see cref="StateDialect.For"/> for its id.
/// </para>
/// </summary>
public sealed class StateDialectRegistry
{
    private readonly Dictionary<string, StateDialect> _dialects = new(StringComparer.Ordinal);

    /// <summary>The process-wide registry every <see cref="StateDialect.For"/> call consults. Not a
    /// property with a private setter — a plugin loader and a test fixture standing up an isolated
    /// registry both have a legitimate reason to hold their own instance, so the constructor stays
    /// public; this is just the one every built-in already lives in.</summary>
    public static StateDialectRegistry Default { get; } = BuildDefault();

    public void Register(StateDialect dialect) => _dialects[dialect.Engine] = dialect;

    /// <summary>Registers <see cref="MsSqlStateDialect"/> and <see cref="PostgresStateDialect"/> bound
    /// to <paramref name="libraries"/> — see this class's own doc comment for why these two, alone among
    /// the built-ins, need an explicit call rather than registering themselves statically.</summary>
    public void RegisterLibraryBackedEngines(LibraryRegistry libraries)
    {
        Register(new MsSqlStateDialect(libraries));
        Register(new PostgresStateDialect(libraries));
    }

    public StateDialect Get(string engine) =>
        TryGet(engine, out var dialect)
            ? dialect
            : throw new InvalidOperationException(
                $"Unknown state engine '{engine}'. Built-in engines are '{StateEngineIds.Sqlite}', " +
                $"'{StateEngineIds.MsSql}' and '{StateEngineIds.Postgres}'; anything else must be " +
                "registered by a compiled StateDialect plugin before this is asked for.");

    public bool TryGet(string engine, [NotNullWhen(true)] out StateDialect? dialect) =>
        _dialects.TryGetValue(engine, out dialect);

    private static StateDialectRegistry BuildDefault()
    {
        var registry = new StateDialectRegistry();
        registry.Register(SqliteStateDialect.Instance);
        return registry;
    }
}
