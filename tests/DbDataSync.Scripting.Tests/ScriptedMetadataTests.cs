using System.Data.Common;
using DbDataSync.Core.Config;
using DbDataSync.Drivers.Abstractions;
using DbDataSync.Scripting;
using DbDataSync.Scripting.Abstractions;
using DbDataSync.Core.Sql;

namespace DbDataSync.Scripting.Tests;

/// <summary>
/// A connection's bound metadata provider, in front of the driver's own catalog. Driven through
/// genuinely compiled scripts.
/// </summary>
public sealed class ScriptedMetadataTests : IDisposable
{
    private readonly string _repoRoot = Path.Combine(Path.GetTempPath(), $"ds-md-{Guid.NewGuid():N}");
    private readonly ConfigRepository _configRepository;
    private readonly ScriptedMetadata _metadata;
    private readonly FakeDriver _driver = new();

    public ScriptedMetadataTests()
    {
        Directory.CreateDirectory(_repoRoot);
        LibGit2Sharp.Repository.Init(_repoRoot);
        _configRepository = new ConfigRepository(
            Path.Combine(_repoRoot, "config"),
            new Core.Git.GitCommitService(_repoRoot),
            new ClrKernel.Core.Secrets.SecretStore(true));
        var host = new ScriptHost(
            _configRepository, new ScriptCompiler(new ScriptCacheDirectory(Path.Combine(_repoRoot, "cache"))));
        _metadata = new ScriptedMetadata(host, _configRepository);
    }

    public void Dispose()
    {
        try { Directory.Delete(_repoRoot, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private const string ConnectionName = "src";

    private void SaveConnection(string? boundScript)
    {
        var input = new ConnectionInput
        {
            Name = ConnectionName, DriverType = ConnectionDriverType.MsSql, Host = "h", AuthMode = AuthMode.IntegratedAuth,
        };
        if (boundScript is not null)
            input.Scripts[ScriptSlots.MetadataProvider] = new ScriptBinding { ScriptName = boundScript };
        _configRepository.SaveConnection(input, new Core.Git.GitAuthor("t", "t@t"));
    }

    private void SaveScript(string name, string entryType, string code) =>
        _configRepository.SaveScript(
            new ScriptDefinition
            {
                Manifest = new ScriptConfig { Name = name, Kind = ScriptSlots.MetadataProvider, EntryType = entryType },
                Code = code,
            },
            new Core.Git.GitAuthor("t", "t@t"));

    private Task<IReadOnlyList<ColumnMetadata>> ColumnsAsync() =>
        _metadata.ListColumnsAsync(
            ConnectionName, null!, _driver, new FakeDialect(), "App", "dbo", "People", CancellationToken.None);

    [Fact]
    public async Task WithNothingBound_TheDriverAnswers()
    {
        SaveConnection(null);

        Assert.Equal(["Id", "First", "Last", "Aud_By"], (await ColumnsAsync()).Select(c => c.Name));
        // And nothing wrapped it: the no-script path is the driver's own call.
        Assert.Equal(1, _driver.ColumnCalls);
    }

    [Fact]
    public async Task AProviderCanFilterTheDriversAnswer()
    {
        // The common case, and the reason the driver's answer reaches the script as a delegate rather
        // than being reimplemented inside it.
        SaveScript("hide-audit", "HideAudit", """
            using System.Collections.Generic;
            using System.Linq;
            using System.Threading;
            using System.Threading.Tasks;
            using DbDataSync.Drivers.Abstractions;
            using DbDataSync.Scripting.Abstractions;

            public sealed class HideAudit : IMetadataProvider
            {
                public Task<IReadOnlyList<string>> ListDatabasesAsync(MetadataContext c, CancellationToken ct) =>
                    c.DriverDatabases(ct);

                public Task<IReadOnlyList<TableMetadata>> ListTablesAsync(MetadataContext c, string database, CancellationToken ct) =>
                    c.DriverTables(database, ct);

                public async Task<IReadOnlyList<ColumnMetadata>> ListColumnsAsync(
                    MetadataContext c, string database, string schema, string table, CancellationToken ct)
                {
                    var columns = await c.DriverColumns(database, schema, table, ct);
                    return columns.Where(x => !x.Name.StartsWith("Aud_")).ToList();
                }
            }
            """);
        SaveConnection("hide-audit");

        Assert.Equal(["Id", "First", "Last"], (await ColumnsAsync()).Select(c => c.Name));
    }

    [Fact]
    public async Task AProviderCanSynthesiseColumnsTheCatalogDoesNotHave()
    {
        // The composition phase 29 rests on: the provider surfaces the column so an operator can map
        // it, and a transform supplies its SQL. The pipeline's catalog never needs to know.
        SaveScript("full-name", "FullName", """
            using System.Collections.Generic;
            using System.Linq;
            using System.Threading;
            using System.Threading.Tasks;
            using DbDataSync.Drivers.Abstractions;
            using DbDataSync.Scripting.Abstractions;

            public sealed class FullName : IMetadataProvider
            {
                public Task<IReadOnlyList<string>> ListDatabasesAsync(MetadataContext c, CancellationToken ct) =>
                    c.DriverDatabases(ct);

                public Task<IReadOnlyList<TableMetadata>> ListTablesAsync(MetadataContext c, string database, CancellationToken ct) =>
                    c.DriverTables(database, ct);

                public async Task<IReadOnlyList<ColumnMetadata>> ListColumnsAsync(
                    MetadataContext c, string database, string schema, string table, CancellationToken ct)
                {
                    var columns = (await c.DriverColumns(database, schema, table, ct)).ToList();
                    columns.Add(new ColumnMetadata("FullName", "nvarchar(200)", true, false, false));
                    return columns;
                }
            }
            """);
        SaveConnection("full-name");

        Assert.Contains("FullName", (await ColumnsAsync()).Select(c => c.Name));
    }

    [Fact]
    public async Task AProviderThatIgnoresTheDriver_NeverCallsIt()
    {
        // The delegate matters most here: for ODBC and JDBC the driver's own answer may not work at
        // all, so a provider must be able to replace it outright without paying for it.
        SaveScript("invented", "Invented", """
            using System.Collections.Generic;
            using System.Threading;
            using System.Threading.Tasks;
            using DbDataSync.Drivers.Abstractions;
            using DbDataSync.Scripting.Abstractions;

            public sealed class Invented : IMetadataProvider
            {
                public Task<IReadOnlyList<string>> ListDatabasesAsync(MetadataContext c, CancellationToken ct) =>
                    Task.FromResult<IReadOnlyList<string>>(new[] { "Invented" });

                public Task<IReadOnlyList<TableMetadata>> ListTablesAsync(MetadataContext c, string database, CancellationToken ct) =>
                    Task.FromResult<IReadOnlyList<TableMetadata>>(new[] { new TableMetadata("app", "Thing") });

                public Task<IReadOnlyList<ColumnMetadata>> ListColumnsAsync(
                    MetadataContext c, string database, string schema, string table, CancellationToken ct) =>
                    Task.FromResult<IReadOnlyList<ColumnMetadata>>(
                        new[] { new ColumnMetadata("OnlyOne", "int", false, true, false) });
            }
            """);
        SaveConnection("invented");

        Assert.Equal(["OnlyOne"], (await ColumnsAsync()).Select(c => c.Name));
        Assert.Equal(0, _driver.ColumnCalls);
    }

    [Fact]
    public async Task AProviderThatThrows_SaysWhatItWasDoing()
    {
        SaveScript("boom", "Boom", """
            using System.Collections.Generic;
            using System.Threading;
            using System.Threading.Tasks;
            using DbDataSync.Drivers.Abstractions;
            using DbDataSync.Scripting.Abstractions;

            public sealed class Boom : IMetadataProvider
            {
                public Task<IReadOnlyList<string>> ListDatabasesAsync(MetadataContext c, CancellationToken ct) =>
                    c.DriverDatabases(ct);
                public Task<IReadOnlyList<TableMetadata>> ListTablesAsync(MetadataContext c, string database, CancellationToken ct) =>
                    c.DriverTables(database, ct);
                public Task<IReadOnlyList<ColumnMetadata>> ListColumnsAsync(
                    MetadataContext c, string database, string schema, string table, CancellationToken ct) =>
                    throw new System.InvalidOperationException("nope");
            }
            """);
        SaveConnection("boom");

        var ex = await Assert.ThrowsAsync<ScriptExecutionException>(ColumnsAsync);
        Assert.Contains("dbo.People", ex.Message);
        Assert.Contains("nope", ex.Message);
    }

    [Fact]
    public async Task AnUnknownConnection_FallsThroughToTheDriver()
    {
        // Nothing saved: resolution finds no connection, so there is nothing bound and the driver
        // answers. Failing here would break browsing for a connection being created.
        var columns = await _metadata.ListColumnsAsync(
            "never-saved", null!, _driver, new FakeDialect(), "App", "dbo", "People", CancellationToken.None);

        Assert.Equal(4, columns.Count);
    }

    [Fact]
    public void MetadataProvider_BindsOnlyAtTheConnection()
    {
        Assert.True(ScriptSlots.IsBindableAt(ScriptSlots.MetadataProvider, BindingLevels.Connection));
        Assert.False(ScriptSlots.IsBindableAt(ScriptSlots.MetadataProvider, BindingLevels.Replication));
        Assert.False(ScriptSlots.IsBindableAt(ScriptSlots.MetadataProvider, BindingLevels.Mapping));

        // Every other slot binds everywhere.
        Assert.True(ScriptSlots.IsBindableAt(ScriptSlots.RowTransform, BindingLevels.Mapping));
        Assert.True(ScriptSlots.IsBindableAt(ScriptSlots.SqlColumnExpression, BindingLevels.Connection));
    }

    private sealed class FakeDriver : IDriver
    {
        public int ColumnCalls { get; private set; }

        public ConnectionDriverType DriverType => ConnectionDriverType.MsSql;
        public IReadOnlyList<IChangeReader> Readers { get; } = [];
        public IReadOnlyList<IStagingProvider> StagingProviders { get; } = [];
        public IReadOnlyList<IChangeWriter> Writers { get; } = [];

        public DbConnection CreateConnection(ConnectionConfig connection, string? credential) => throw new NotSupportedException();

        public Task<IReadOnlyList<string>> ListDatabasesAsync(DbConnection connection, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<string>>(["App"]);

        public Task<IReadOnlyList<TableMetadata>> ListTablesAsync(
            DbConnection connection, string database, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<TableMetadata>>([new TableMetadata("dbo", "People")]);

        public Task<IReadOnlyList<ColumnMetadata>> ListColumnsAsync(
            DbConnection connection, string database, string schema, string table, CancellationToken cancellationToken)
        {
            ColumnCalls++;
            return Task.FromResult<IReadOnlyList<ColumnMetadata>>(
            [
                new("Id", "int", false, true, true),
                new("First", "nvarchar(50)", true, false, false),
                new("Last", "nvarchar(50)", true, false, false),
                new("Aud_By", "nvarchar(50)", true, false, false),
            ]);
        }
    }

    private sealed class FakeDialect : IScriptDialect
    {
        public string EngineName => "MsSql";
        public string QuoteIdentifier(string identifier) => $"[{identifier}]";
        public string ParameterReference(string name) => $"@{name}";
        public CanonicalType ToCanonicalType(string nativeType) => throw new NotSupportedException();
        public RenderedColumnType RenderColumnType(CanonicalType type) => throw new NotSupportedException();
    }
}
