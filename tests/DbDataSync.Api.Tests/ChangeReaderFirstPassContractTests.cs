using System.Reflection;
using DbDataSync.Core.Config;
using DbDataSync.Core.Sql;
using DbDataSync.Drivers.Abstractions;
using DbDataSync.Drivers.DuckDb;
using DbDataSync.Drivers.Generic;
using DbDataSync.Drivers.MsSql;
using DbDataSync.Scripting;
using Xunit;

namespace DbDataSync.Api.Tests;

/// <summary>
/// Every change reader has had a decision made about which <see cref="ReadIntent"/> values it can
/// honour, and the decision is enumerated from the assemblies rather than from a list somebody
/// remembers to update — see `architecture/detailed-design.md` §4.1 and
/// `architecture/implementation/done/phase-101-readers-honour-the-read-intent.md`.
/// <para>
/// **This test evolved rather than died** when phase 101's §1 was retargeted by
/// `architecture/planning/done/bulk-load-pipeline-and-the-initial-load-rule.md`. It used to classify
/// each reader as full-load-on-first-pass or exempt from that rule; now that a stored
/// <see cref="ReadIntent"/> decides what a pass does rather than a null watermark, what needs pinning is
/// that every reader has declared which intents it honours through <see cref="IReadIntentDeclaring"/>,
/// completely and honestly — not that a particular reader still branches on
/// <see cref="ReadIntent.InitialLoad"/> internally. **This does not assert readers stop full-loading on
/// InitialLoad** — deleting those branches is the Bulk Load pipeline's own future phase (see the
/// bulk-load doc's Phase B), not this one.
/// </para>
/// <para>
/// The property defended is unchanged from phase 99's original version: a new reader cannot be added
/// without somebody deciding. It lives here for the same reason that version did — this is the only
/// test project that sees every driver assembly.
/// </para>
/// </summary>
public sealed class ChangeReaderFirstPassContractTests
{
    /// <summary>
    /// Readers that declare <see cref="IReadIntentDeclaring"/>, mapped to the test that proves their
    /// declared set is correct against a real database. A name here is a claim that the test exists and
    /// covers this; <see cref="EveryDeclaredProofNamesATestThatExists"/> checks the claim.
    /// </summary>
    private static readonly Dictionary<Type, string> Declaring = new()
    {
        [typeof(MsSqlChangeTrackingReader)] =
            "DbDataSync.Drivers.MsSql.Tests.MsSqlChangeTrackingReaderTests.FullLoad_WhenNoPreviousWatermark_ReturnsAllRowsAsInserts",
        [typeof(MsSqlCdcReader)] =
            "DbDataSync.Drivers.MsSql.Tests.MsSqlCdcReaderTests.WithNoStoredPosition_EveryRowIsReadAsAnInsert",
        [typeof(TriggerAuditReader)] =
            "DbDataSync.Drivers.MsSql.Tests.TriggerAuditReaderTests.WithNoStoredPosition_EveryRowIsReadAsAnInsert",
        [typeof(WatermarkReader)] =
            "DbDataSync.Drivers.MsSql.Tests.MsSqlWatermarkReaderTests.FullLoad_ReturnsAllRows",
    };

    /// <summary>
    /// Readers with nothing honest to declare, and why. Being here is a decision, not an oversight —
    /// which is the whole point of the exemption being written down beside the rule rather than
    /// inferred from a reader's silence. Per <see cref="IReadIntentDeclaring"/>'s own doc comment, a
    /// reader with nothing to say about any intent should not implement the interface at all — so every
    /// reader named here must NOT implement it, checked by
    /// <see cref="ExemptReaders_DoNotImplementTheInterface"/>.
    /// </summary>
    private static readonly Dictionary<Type, string> Exempt = new()
    {
        [typeof(BatchReloadReader)] =
            "A reload reads every row by definition; its entire purpose is to re-read rows an "
            + "incremental pass has already seen, so it ignores the watermark rather than branching on "
            + "it. Its only checkmark in the original matrix was InitialLoad, which stopped being a "
            + "per-reader question when the bulk-load retarget made it universally available.",
        [typeof(MsSqlBatchReloadReader)] =
            "The same contract as BatchReloadReader, in the engine-specific form.",
        [typeof(ScriptedQueryReader)] =
            "A query source has no change feed to have been switched on, and no catalog behind it. The "
            + "watermark is handed to the script, which decides what it means — there is no first-pass "
            + "branch for this reader to get wrong, and no intent for it to honestly declare.",
        [typeof(DuckDbQueryReader)] =
            "As ScriptedQueryReader: a query source, not a feed over a table with pre-existing rows.",
        [typeof(KeyReconcileReader)] =
            "A delete-diff key sweep reads every key in scope by definition, same as BatchReloadReader — "
            + "it ignores the watermark rather than branching on it, so there is nothing honest to "
            + "declare about Changes/ChangesFromEarliest/ChangesFromLatest.",
    };

    /// <summary>
    /// Named explicitly rather than swept from <see cref="AppDomain"/>, because assemblies load lazily:
    /// a sweep of what happens to be loaded would quietly cover fewer readers than it appears to, which
    /// is the failure mode this whole file exists to prevent.
    /// </summary>
    private static readonly Assembly[] DriverAssemblies =
    [
        typeof(MsSqlDriver).Assembly,
        typeof(BatchReloadReader).Assembly,
        typeof(DuckDbDriver).Assembly,
        typeof(ScriptedQueryReader).Assembly,
        typeof(Drivers.Postgres.PostgresDriver).Assembly,
    ];

    private static List<Type> AllReaders() =>
        DriverAssemblies
            .SelectMany(a => a.GetTypes())
            .Where(t => typeof(IChangeReader).IsAssignableFrom(t) && t is { IsAbstract: false, IsInterface: false })
            .OrderBy(t => t.FullName, StringComparer.Ordinal)
            .ToList();

    /// <summary>
    /// The assertion that matters. A reader in neither list is a reader nobody decided about, and the
    /// message says what the decision is between rather than only that one is missing.
    /// </summary>
    [Fact]
    public void EveryChangeReader_IsDeclaringOrExempt()
    {
        var readers = AllReaders();

        // A sweep that found nothing would pass every assertion below it.
        Assert.NotEmpty(readers);

        var undeclared = readers
            .Where(t => !Declaring.ContainsKey(t) && !Exempt.ContainsKey(t))
            .ToList();

        Assert.True(undeclared.Count == 0,
            $"These IChangeReader implementations have no declared read-intent contract: "
            + $"{string.Join(", ", undeclared.Select(t => t.Name))}. See "
            + "architecture/implementation/done/phase-101-readers-honour-the-read-intent.md. A reader "
            + "either implements IReadIntentDeclaring and names the test proving its declared set, or "
            + "is exempt with the reason it has nothing honest to declare. Add it to Declaring with the "
            + "test that proves it, or to Exempt with the reason.");

        // Both at once would mean the two lists disagree about the same reader.
        var both = readers.Where(t => Declaring.ContainsKey(t) && Exempt.ContainsKey(t)).ToList();
        Assert.True(both.Count == 0,
            $"Declared as both declaring and exempt: {string.Join(", ", both.Select(t => t.Name))}.");
    }

    /// <summary>
    /// The two lists describe readers that exist. A stale entry left behind by a rename or a deletion
    /// makes the coverage above read as broader than it is.
    /// </summary>
    [Fact]
    public void NeitherListNamesAReaderThatIsGone()
    {
        var readers = AllReaders().ToHashSet();

        var stale = Declaring.Keys.Concat(Exempt.Keys).Where(t => !readers.Contains(t)).ToList();

        Assert.True(stale.Count == 0,
            $"Declared but no longer an IChangeReader: {string.Join(", ", stale.Select(t => t.Name))}.");
    }

    /// <summary>
    /// Every reader named in <see cref="Declaring"/> really implements <see cref="IReadIntentDeclaring"/>,
    /// its set is non-empty, and — the whole point of phase 101's retarget — it never contains
    /// <see cref="ReadIntent.InitialLoad"/>. That intent stopped being a per-reader question once the
    /// Bulk Load pipeline made it universally available; a reader still declaring it would be reverting
    /// the retarget silently.
    /// </summary>
    [Fact]
    public void DeclaringReaders_HaveANonEmpty_InitialLoadFreeSet()
    {
        foreach (var type in Declaring.Keys)
        {
            var instance = CreateUninitialized(type);
            var declaring = Assert.IsAssignableFrom<IReadIntentDeclaring>(instance);

            Assert.True(declaring.SupportedIntents.Count > 0,
                $"{type.Name} implements IReadIntentDeclaring but declares an empty set — a reader with " +
                "nothing to declare should not implement the interface at all (see Exempt).");

            Assert.False(declaring.SupportedIntents.Contains(ReadIntent.InitialLoad),
                $"{type.Name} still declares ReadIntent.InitialLoad. Per phase 101's retarget, InitialLoad " +
                "is no longer a per-reader question — remove it from SupportedIntents.");
        }
    }

    /// <summary>
    /// Every reader named in <see cref="Exempt"/> really has nothing to declare — it must not implement
    /// <see cref="IReadIntentDeclaring"/> at all, the same convention <see cref="ISegmentExpandingReader"/>
    /// already follows for a reader that cannot expand a segment.
    /// </summary>
    [Fact]
    public void ExemptReaders_DoNotImplementTheInterface()
    {
        var stillImplementing = Exempt.Keys.Where(t => typeof(IReadIntentDeclaring).IsAssignableFrom(t)).ToList();

        Assert.True(stillImplementing.Count == 0,
            "These readers are listed as exempt (nothing honest to declare) but still implement " +
            $"IReadIntentDeclaring: {string.Join(", ", stillImplementing.Select(t => t.Name))}. Either " +
            "remove the interface from the reader, or move it to Declaring with a real declared set.");
    }

    /// <summary>
    /// A named proof has to be a test that is really there. Checked by reflection over the driver test
    /// assemblies, so a renamed or deleted test is caught here rather than leaving this file asserting
    /// that something somewhere covers it.
    /// </summary>
    [Fact]
    public void EveryDeclaredProofNamesATestThatExists()
    {
        // Loaded by path: the driver test projects are siblings of this one and are not referenced by
        // it, which is deliberate — a reference would drag every driver's test fixtures into this
        // assembly to check three names.
        //
        // Real bin/ output only, never obj/ — a real, filesystem-enumeration-order-dependent failure
        // found in CI (never reproduced locally, where bin/ always happened to enumerate first): MSBuild
        // leaves a *second* copy of each driver test assembly in obj/{Configuration}/{TFM}/ (the
        // compiler's own pre-copy output), whose immediate directory is also named after the TFM but
        // which never receives a project's copied dependencies (Npgsql, Microsoft.Data.SqlClient, ...) —
        // only bin/ does. The original filter (`f.Directory!.Name == here.Name`) matched both. Whichever
        // copy `Assembly.LoadFrom` reaches *first* wins the assembly's identity for the whole process
        // (a later `LoadFrom` of the same identity from a different path returns the already-loaded
        // instance rather than reloading) — so on a filesystem that happens to enumerate obj/ before
        // bin/, the obj/ copy loads, and the later `GetTypes()` call throws `ReflectionTypeLoadException`
        // for a dependency that was never missing from the real build output at all. Requiring the
        // grandparent-of-grandparent segment to be literally "bin" (the real `bin/{Configuration}/{TFM}/`
        // layout) excludes obj/ outright, regardless of enumeration order.
        var here = new FileInfo(typeof(ChangeReaderFirstPassContractTests).Assembly.Location).Directory!;
        var testAssemblies = here.Parent!.Parent!.Parent!.Parent!
            .EnumerateFiles("DbDataSync.Drivers.*.Tests.dll", SearchOption.AllDirectories)
            .Where(f => f.Directory!.Name == here.Name && f.Directory.Parent?.Parent?.Name == "bin")
            .Select(f => f.FullName)
            .Distinct()
            .Select(Assembly.LoadFrom)
            .ToList();

        Assert.NotEmpty(testAssemblies);

        var methods = testAssemblies
            .SelectMany(a => a.GetTypes())
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .Select(m => $"{t.FullName}.{m.Name}"))
            .ToHashSet(StringComparer.Ordinal);

        var missing = Declaring
            .Where(pair => !methods.Contains(pair.Value))
            .Select(pair => $"{pair.Key.Name} → {pair.Value}")
            .ToList();

        Assert.True(missing.Count == 0,
            $"Declared proofs that no longer exist: {string.Join("; ", missing)}. Either the test was "
            + "renamed, in which case update the name here, or it was deleted, in which case this "
            + "reader's declared intents are no longer covered by anything.");
    }

    /// <summary>
    /// Every reader here is constructed with a real dialect and null-but-unused catalog/binder
    /// dependencies, rather than via reflection's uninitialized-object trick: <c>SupportedIntents</c> is
    /// a property initialised in the class body, which does not run at all unless the constructor does
    /// — so an uninitialized instance would read as an empty set rather than as the real declaration.
    /// The catalog and segment-value-binder dependencies are never touched by that initializer, so null
    /// stands in for them here.
    /// </summary>
    private static object CreateUninitialized(Type type)
    {
        if (type == typeof(MsSqlChangeTrackingReader) || type == typeof(MsSqlCdcReader))
            return Activator.CreateInstance(type)!;

        if (type == typeof(TriggerAuditReader))
            return Activator.CreateInstance(type, MsSqlDialect.Instance, null)!;

        if (type == typeof(WatermarkReader))
            return Activator.CreateInstance(type, MsSqlDialect.Instance, null, null)!;

        throw new InvalidOperationException(
            $"No construction recipe for '{type.Name}' — add one beside this method.");
    }
}
