using System.Reflection;
using DbDataSync.Drivers.Abstractions;
using DbDataSync.Drivers.DuckDb;
using DbDataSync.Drivers.Generic;
using DbDataSync.Drivers.MsSql;
using DbDataSync.Scripting;
using Xunit;

namespace DbDataSync.Api.Tests;

/// <summary>
/// Every change reader has had a decision made about what it does on a pass with no stored watermark,
/// and the decision is enumerated from the assemblies rather than from a list somebody remembers to
/// update — see `architecture/detailed-design.md` §4.1 for the rule and why it exists.
/// <para>
/// **The rule is implemented independently in each reader**, which is what makes this worth pinning: a
/// change feed only knows about changes since it was switched on, so a reader that skips the full load
/// leaves a mapping permanently and silently half-replicated against a source table that already had
/// rows. There is no shared base class to enforce it and no single call site to review. The failure is
/// invisible — the pass succeeds, the counts look plausible, and the rows that were there before
/// anybody enabled the feed simply never arrive.
/// </para>
/// <para>
/// This test does not exercise the readers; the per-reader tests named below do that, against real
/// databases. What it enforces is that a *new* reader cannot be added without somebody deciding which
/// of the two contracts it follows.
/// </para>
/// <para>
/// It lives here because this is the only test project that sees every driver assembly — the Api
/// references them all, and each driver's own test project sees only itself. Same reason, and the same
/// shape, as <see cref="AuthorizationCoverageTests"/>.
/// </para>
/// </summary>
public sealed class ChangeReaderFirstPassContractTests
{
    /// <summary>
    /// Readers that read the whole source table when there is no stored watermark, mapped to the test
    /// that proves it against a real database. A name here is a claim that the test exists and covers
    /// this; <see cref="EveryDeclaredProofNamesATestThatExists"/> checks the claim.
    /// </summary>
    private static readonly Dictionary<Type, string> FullLoadOnFirstPass = new()
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
    /// Readers the rule does not apply to, and why. Being here is a decision, not an oversight — which
    /// is the whole point of the exemption being written down beside the rule rather than inferred from
    /// a reader's silence.
    /// </summary>
    private static readonly Dictionary<Type, string> Exempt = new()
    {
        [typeof(BatchReloadReader)] =
            "A reload reads every row by definition; its entire purpose is to re-read rows an "
            + "incremental pass has already seen, so it ignores the watermark rather than branching on it.",
        [typeof(MsSqlBatchReloadReader)] =
            "The same contract as BatchReloadReader, in the engine-specific form.",
        [typeof(ScriptedQueryReader)] =
            "A query source has no change feed to have been switched on, and no catalog behind it. The "
            + "watermark is handed to the script, which decides what it means — there is no first-pass "
            + "branch for this reader to get wrong.",
        [typeof(DuckDbQueryReader)] =
            "As ScriptedQueryReader: a query source, not a feed over a table with pre-existing rows.",
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
    /// The assertion that matters. A reader in neither table is a reader nobody decided about, and the
    /// message says what the decision is between rather than only that one is missing.
    /// </summary>
    [Fact]
    public void EveryChangeReader_IsDeclaredEitherFullLoadOnFirstPass_OrExempt()
    {
        var readers = AllReaders();

        // A sweep that found nothing would pass every assertion below it.
        Assert.NotEmpty(readers);

        var undeclared = readers
            .Where(t => !FullLoadOnFirstPass.ContainsKey(t) && !Exempt.ContainsKey(t))
            .ToList();

        Assert.True(undeclared.Count == 0,
            $"These IChangeReader implementations have no declared first-pass contract: "
            + $"{string.Join(", ", undeclared.Select(t => t.Name))}. See architecture/detailed-design.md §4.1. "
            + "A reader over a change feed must read the whole source table when there is no stored "
            + "watermark — the feed only holds what has happened since it was switched on, so without "
            + "that first pass the mapping is silently half-replicated. Add it to FullLoadOnFirstPass "
            + "with the test that proves it, or to Exempt with the reason it does not apply.");

        // Both at once would mean the two tables disagree about the same reader.
        var both = readers.Where(t => FullLoadOnFirstPass.ContainsKey(t) && Exempt.ContainsKey(t)).ToList();
        Assert.True(both.Count == 0,
            $"Declared as both full-load-on-first-pass and exempt: {string.Join(", ", both.Select(t => t.Name))}.");
    }

    /// <summary>
    /// The two tables describe readers that exist. A stale entry left behind by a rename or a deletion
    /// makes the coverage above read as broader than it is.
    /// </summary>
    [Fact]
    public void NeitherTableNamesAReaderThatIsGone()
    {
        var readers = AllReaders().ToHashSet();

        var stale = FullLoadOnFirstPass.Keys.Concat(Exempt.Keys).Where(t => !readers.Contains(t)).ToList();

        Assert.True(stale.Count == 0,
            $"Declared but no longer an IChangeReader: {string.Join(", ", stale.Select(t => t.Name))}.");
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
        var here = new FileInfo(typeof(ChangeReaderFirstPassContractTests).Assembly.Location).Directory!;
        var testAssemblies = here.Parent!.Parent!.Parent!.Parent!
            .EnumerateFiles("DbDataSync.Drivers.*.Tests.dll", SearchOption.AllDirectories)
            .Where(f => f.Directory!.Name == here.Name)
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

        var missing = FullLoadOnFirstPass
            .Where(pair => !methods.Contains(pair.Value))
            .Select(pair => $"{pair.Key.Name} → {pair.Value}")
            .ToList();

        Assert.True(missing.Count == 0,
            $"Declared proofs that no longer exist: {string.Join("; ", missing)}. Either the test was "
            + "renamed, in which case update the name here, or it was deleted, in which case this "
            + "reader's first-pass behaviour is no longer covered by anything.");
    }
}
