using System.Data.Common;
using DbDataSync.Core.Config;
using DbDataSync.Drivers.Abstractions;

namespace DbDataSync.Drivers.Generic.Tests;

/// <summary>Minimal <see cref="IGenericDriverSpec"/> for exercising <see cref="GenericDriverBase{TSpec}"/>'s
/// reader/staging/writer construction directly, without any of <see cref="GenericDriverSpec"/>'s
/// ADO.NET-specific fields (a provider factory, connection-string keys) that this has no use for.</summary>
internal sealed record FakeGenericDriverSpec(
    string Id, DbDataSync.Core.Sql.SqlDialect Dialect, IDescriptorCatalog Catalog,
    IReadOnlyList<string> Readers, IReadOnlyList<string> Staging, IReadOnlyList<string> Writers,
    ISegmentValueBinder? ValueBinder = null, string? DisplayName = null, string? TestQuery = null) : IGenericDriverSpec;

/// <summary>The thinnest possible <see cref="GenericDriverBase{TSpec}"/> — connection-related members
/// are never exercised by these tests, which only construct the driver to see what its own
/// Readers/StagingProviders/Writers properties came out as.</summary>
internal sealed class FakeGenericDriver(FakeGenericDriverSpec spec)
    : GenericDriverBase<FakeGenericDriverSpec>(spec, new RecordingBinder())
{
    public override DbConnection CreateConnection(ConnectionConfig connection, string? credential) =>
        throw new NotSupportedException();
    public override Task<IReadOnlyList<string>> ListDatabasesAsync(DbConnection connection, CancellationToken cancellationToken) =>
        throw new NotSupportedException();
    protected override Task SwitchDatabaseAsync(DbConnection connection, string database, CancellationToken cancellationToken) =>
        throw new NotSupportedException();
}

/// <summary>
/// <see cref="GenericDriverBase{TSpec}.SupportedReaderKinds"/>/<c>SupportedStagingKinds</c>/
/// <c>SupportedWriterKinds</c> — the driver-authoring UI's <c>GET /api/known-driver-kinds</c> reads these
/// directly, on the premise that they structurally cannot drift from what <c>BuildReaders</c>/
/// <c>BuildStaging</c>/<c>BuildWriters</c> actually accept (both derive from the same factory
/// dictionaries). These tests prove that premise rather than assume it.
/// </summary>
public sealed class GenericDriverKindCatalogTests
{
    private static FakeGenericDriverSpec Spec(IReadOnlyList<string> readers, IReadOnlyList<string> staging, IReadOnlyList<string> writers) =>
        new("test", BracketDialect.Instance, new InformationSchemaQueries(BracketDialect.Instance), readers, staging, writers);

    [Fact]
    public void EveryReportedReaderKind_IsAcceptedByBuildReaders()
    {
        var driver = new FakeGenericDriver(Spec(GenericDriverBase<FakeGenericDriverSpec>.SupportedReaderKinds, [], []));

        Assert.Equal(GenericDriverBase<FakeGenericDriverSpec>.SupportedReaderKinds.Count, driver.Readers.Count);
    }

    [Fact]
    public void EveryReportedStagingKind_IsAcceptedByBuildStaging()
    {
        var driver = new FakeGenericDriver(Spec([], GenericDriverBase<FakeGenericDriverSpec>.SupportedStagingKinds, []));

        Assert.Equal(GenericDriverBase<FakeGenericDriverSpec>.SupportedStagingKinds.Count, driver.StagingProviders.Count);
    }

    [Fact]
    public void EveryReportedWriterKind_IsAcceptedByBuildWriters()
    {
        var driver = new FakeGenericDriver(Spec([], [], GenericDriverBase<FakeGenericDriverSpec>.SupportedWriterKinds));

        Assert.Equal(GenericDriverBase<FakeGenericDriverSpec>.SupportedWriterKinds.Count, driver.Writers.Count);
    }

    /// <summary>The regression this whole catalog exists to prevent, pinned directly: found live while
    /// building the driver-authoring UI's own known-kinds endpoint — <c>PostgresDriver</c> constructs a
    /// <c>KeyReconcileScd2CloseWriter</c> directly (its own hand-written <c>Writers</c> list, identical
    /// <c>(dialect, catalog, binder)</c> constructor shape), but <c>GenericDriverBase</c>'s writer
    /// factories had no entry for it at all — a driver.yaml listing it in <c>writers:</c> would have
    /// thrown <see cref="ArgumentException"/> at construction. Fixed by adding the missing factory entry,
    /// not by excluding the kind: this pins that it now constructs a real
    /// <see cref="KeyReconcileScd2CloseWriter"/> through the generic dictionary path, the same object the
    /// hand-written path already produces.
    /// </summary>
    [Fact]
    public void KeyReconcileScd2Close_IsAGenericWriterKind()
    {
        Assert.Contains(GenericDriverKinds.KeyReconcileScd2Close, GenericDriverBase<FakeGenericDriverSpec>.SupportedWriterKinds);

        var driver = new FakeGenericDriver(Spec([], [], [GenericDriverKinds.KeyReconcileScd2Close]));

        var writer = Assert.Single(driver.Writers);
        Assert.IsType<KeyReconcileScd2CloseWriter>(writer);
        Assert.Equal(GenericDriverKinds.KeyReconcileScd2Close, writer.Kind);
    }

    [Fact]
    public void AnUnknownKind_ThrowsForEachCategory()
    {
        Assert.Throws<ArgumentException>(() => new FakeGenericDriver(Spec(["Bogus"], [], [])));
        Assert.Throws<ArgumentException>(() => new FakeGenericDriver(Spec([], ["Bogus"], [])));
        Assert.Throws<ArgumentException>(() => new FakeGenericDriver(Spec([], [], ["Bogus"])));
    }
}
