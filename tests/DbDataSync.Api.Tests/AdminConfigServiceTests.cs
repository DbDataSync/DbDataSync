using ClrKernel.Core.Secrets;
using DbDataSync.Api.Auth;
using DbDataSync.Api.Configuration;
using DbDataSync.Api.Services;
using DbDataSync.Core.Config;
using DbDataSync.Core.Git;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace DbDataSync.Api.Tests;

/// <summary>
/// <see cref="AdminConfigService"/> exercised directly, without a host — this sandbox's <c>TestServer</c>
/// cannot run any authenticated request once Negotiate is registered (a pre-existing, documented gap;
/// see <c>AdminConfigControllerTests</c>' class doc comment and phase 79's retrospective), which would
/// make every HTTP-level test of this service's logic fail here for a reason that has nothing to do with
/// the logic itself. Building the service's dependencies by hand — the same static
/// <c>FromConfiguration</c> factories <c>DbDataSyncHost.Build</c> itself calls, and the real provider types
/// (<see cref="DbDataSyncConfigFileProvider"/>, <c>EnvironmentVariablesConfigurationProvider</c>) rather
/// than a stand-in — verifies the actual source-resolution, masking and write logic without a host at
/// all.
/// </summary>
public sealed class AdminConfigServiceTests : IDisposable
{
    private readonly string _repoRoot = Directory.CreateTempSubdirectory("admin-config-service-tests-").FullName;

    public void Dispose() => GitTempDirectory.DeleteRecursively(_repoRoot);

    private const string UrlKey = "DbDataSync:App:Url";
    private const string StateConnectionStringKey = "DbDataSync:State:ConnectionString";

    private AdminConfigService Build(IConfiguration? configuration = null)
    {
        configuration ??= new ConfigurationBuilder().Build();
        var apiOptions = ApiOptions.FromConfiguration(configuration);
        return new AdminConfigService(
            configuration,
            apiOptions,
            AuthOptions.FromConfiguration(configuration),
            PasskeyOptions.FromConfiguration(configuration, apiOptions),
            new GitCommitService(_repoRoot),
            SecretStore.ForProviders([new InMemorySecretProvider()]),
            new RestartRequiredState(apiOptions));
    }

    /// <summary>The same precedence InsertConfigFile establishes: the file first, environment variables
    /// after (so either can still override it), matching docs/configuration.md's own documented ordering. Always
    /// carries DbDataSync:App:RepoRoot pointed at this test's own temp directory — otherwise ApiOptions
    /// defaults it to "&lt;cwd&gt;/dbdatasync-repo", which is not where this test's own SetValue/Read
    /// calls (against _repoRoot) are looking.</summary>
    private IConfiguration ConfigurationWithFile(IReadOnlyDictionary<string, string?>? fileData = null)
    {
        var builder = new ConfigurationBuilder();
        if (fileData is not null)
            builder.Add(new DbDataSyncConfigFileSource { InitialData = fileData });
        builder.AddInMemoryCollection(new Dictionary<string, string?> { ["DbDataSync:App:RepoRoot"] = _repoRoot });
        builder.AddEnvironmentVariables();
        return builder.Build();
    }

    /// <summary>
    /// The Admin screen lists "every <c>DbDataSync:*</c> key docs/configuration.md documents" — and nothing checked that the
    /// document and the catalog agree, so a key could ship undocumented (or the reverse) and only a reader would find out
    /// (phase 162). Each key's environment-variable form is what the document's tables list beside it, and the form is
    /// unambiguous: <c>DbDataSync:Auth:Windows:AdminGroup</c> is <c>DbDataSync__Auth__Windows__AdminGroup</c>.
    /// </summary>
    [Fact]
    public void EveryKeyTheAdminScreenListsIsDocumentedInConfigurationMd()
    {
        var docs = File.ReadAllText(Path.Combine(RepoDocsDirectory(), "configuration.md"));
        var service = Build(ConfigurationWithFile());

        var undocumented = service.List()
            .Select(entry => entry.Key.Replace(":", "__"))
            .Where(env => !docs.Contains($"`{env}", StringComparison.Ordinal))
            .ToList();

        Assert.True(undocumented.Count == 0,
            $"docs/configuration.md does not mention: {string.Join(", ", undocumented)}. Add a row for each (the env-var form in backticks).");
    }

    /// <summary>The docs folder of the checkout these tests run from — found by walking up, since the test assembly is
    /// deep under bin/.</summary>
    internal static string RepoDocsDirectory()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "docs");
            if (File.Exists(Path.Combine(candidate, "configuration.md")))
                return candidate;
        }

        throw new DirectoryNotFoundException("docs/configuration.md was not found above " + AppContext.BaseDirectory);
    }

    [Fact]
    public void NothingConfigures_TheKey_SourceIsDefault()
    {
        var service = Build(ConfigurationWithFile());

        var entry = service.Get(UrlKey)!;

        // App:Url now always resolves to ApiOptions.DefaultUrl rather than null — see ApiOptions.Url's
        // own doc comment for why that's a real default now, not a contextual one. Unlike the old
        // contextual-null case, there genuinely is a value here to put in the file, so CanAdopt is true.
        Assert.Equal("default", entry.Source);
        Assert.Equal(ApiOptions.DefaultUrl, entry.Value);
        Assert.False(entry.Editable);
        Assert.True(entry.CanAdopt);
    }

    [Fact]
    public void ADefaultBackedByApiOptions_ShowsTheComputedDefault_NotNull()
    {
        var service = Build(ConfigurationWithFile());

        var entry = service.Get("DbDataSync:State:Engine")!;

        Assert.Equal("default", entry.Source);
        Assert.Equal("Sqlite", entry.Value);
    }

    private const string NotesKey = "DbDataSync:Notes:MarkdownRenderer";

    [Fact]
    public void NotesMarkdownRenderer_IsBasicByDefault_EditableOnceInTheFile_AndCarriesItsWarning()
    {
        var service = Build(ConfigurationWithFile());

        var entry = service.Get(NotesKey)!;

        Assert.Equal("basic", entry.Value);
        Assert.Equal("basic", entry.RunningValue);
        Assert.Equal("basic", entry.DefaultValue);
        // Beside the control in both states, and for this key only — nothing else on the screen carries one.
        Assert.False(string.IsNullOrWhiteSpace(entry.Caution));
        Assert.Contains("other people's sessions", entry.Caution);
        Assert.All(service.List().Where(e => e.Key != NotesKey && e.Key != "DbDataSync:Auth:Network:Admin"
                && e.Key != "DbDataSync:Auth:Network:Viewer"), e => Assert.Null(e.Caution));
    }

    [Fact]
    public void NotesMarkdownRenderer_IsANestedKey_AndStillFileWritable()
    {
        // Phase 164: the "only flat top-level keys are file-writable" claim this test's own predecessor
        // (NotesRichMarkdown_IsAFlatKey_SoTheWriterCanAddressIt) rested on was never actually true for
        // DbDataSyncConfigFile.SetValue's writer — see AdminConfigService's class doc comment.
        var service = Build(ConfigurationWithFile());

        var written = service.Set(NotesKey, "rich", CurrentUser.SystemAuthor)!;

        Assert.Equal("file", written.Source);
        Assert.Equal("rich", written.Value);
        Assert.Equal("basic", written.RunningValue); // running process unchanged until a restart
        Assert.Equal("rich", DbDataSyncConfigFile.Read(_repoRoot)[NotesKey]);
        Assert.Equal(
            NotesRenderer.Rich,
            ApiOptions.FromConfiguration(ConfigurationWithFile(DbDataSyncConfigFile.Read(_repoRoot))).NotesRenderer);
    }

    [Fact]
    public void Writable_IsTheOneCatalogTheCliAndSetupReadToo()
    {
        var key = AdminConfigService.Writable("Notes:MarkdownRenderer");

        Assert.Equal(NotesKey, key!.Key);
        Assert.Equal("basic", key.DefaultValue);
        Assert.NotNull(key.Caution);
        Assert.Equal(key.Key, AdminConfigService.Writable("dbdatasync:notes:markdownrenderer")!.Key);
        Assert.Null(AdminConfigService.Writable("Nope"));
        Assert.Contains("Notes:MarkdownRenderer", AdminConfigService.WritableKeyNames());
    }

    /// <summary>Phase 164's other confirmed finding: a nested key genuinely is file-writable once the
    /// catalog says so — Auth:Windows:AdminGroup is the concrete case that used to be display-only.</summary>
    [Fact]
    public void ANestedAuthKey_IsNowFileWritable()
    {
        var key = AdminConfigService.Writable("Auth:Windows:AdminGroup");

        Assert.NotNull(key);
        Assert.Contains("Auth:Windows:AdminGroup", AdminConfigService.WritableKeyNames());

        var service = Build(ConfigurationWithFile());
        var author = new GitAuthor("Test Admin", "admin@example.com");
        var written = service.Set("DbDataSync:Auth:Windows:AdminGroup", "DBADMINS", author);

        Assert.NotNull(written);
        Assert.Equal("DBADMINS", DbDataSyncConfigFile.Read(_repoRoot)["DbDataSync:Auth:Windows:AdminGroup"]);
    }

    [Fact]
    public void ADurationOrCountKey_ReportsItsUnit_AndAPathOrEngineKeyReportsNone()
    {
        var service = Build(ConfigurationWithFile());

        Assert.Equal("days", service.Get("DbDataSync:State:Retention:RunDays")!.Unit);
        Assert.Equal("runs", service.Get("DbDataSync:State:Retention:RunMaxPerMapping")!.Unit);
        Assert.Equal("minutes", service.Get("DbDataSync:State:Retention:PruningIntervalMinutes")!.Unit);
        Assert.Equal("days", service.Get("DbDataSync:State:Retention:ChangeCheckDays")!.Unit);
        // Not a plain magnitude — an engine name and a filesystem path, not a count of anything.
        Assert.Null(service.Get("DbDataSync:State:Engine")!.Unit);
        Assert.Null(service.Get(UrlKey)!.Unit);
    }

    [Fact]
    public void AFileSourcedKey_IsEditable_AndSetWritesAndCommitsIt()
    {
        DbDataSyncConfigFile.SetValue(_repoRoot, "DbDataSync", "App:Url", "http://initial/");
        var service = Build(ConfigurationWithFile(DbDataSyncConfigFile.Read(_repoRoot)));

        var before = service.Get(UrlKey)!;
        Assert.Equal("file", before.Source);
        Assert.Equal("http://initial/", before.Value);
        Assert.True(before.Editable);
        Assert.False(before.CanAdopt);

        var author = new GitAuthor("Test Admin", "admin@example.com");
        var after = service.Set(UrlKey, "http://changed/", author);

        Assert.NotNull(after);
        Assert.Equal("file", after!.Source);
        Assert.Equal("http://changed/", after.Value);
        Assert.Equal("http://changed/", DbDataSyncConfigFile.Read(_repoRoot)[UrlKey]);

        using var repo = new LibGit2Sharp.Repository(_repoRoot);
        Assert.Contains("DbDataSync:App:Url", repo.Head.Tip.Message);
    }

    /// <summary>Reset is Adopt's inverse: putting the factory default back into the file rather than
    /// taking a non-file value out of it. Offered only while there's something to undo, and Set(key,
    /// DefaultValue) is genuinely all it takes — no separate endpoint or code path.</summary>
    [Fact]
    public void AFileSourcedKeyAwayFromItsDefault_CanBeReset_BackToTheApplicationDefault()
    {
        DbDataSyncConfigFile.SetValue(_repoRoot, "DbDataSync", "State:Engine", "Postgres");
        var service = Build(ConfigurationWithFile(DbDataSyncConfigFile.Read(_repoRoot)));

        var before = service.Get("DbDataSync:State:Engine")!;
        Assert.Equal("Postgres", before.Value);
        Assert.Equal("Sqlite", before.DefaultValue);
        Assert.True(before.CanReset);

        var author = new GitAuthor("Test Admin", "admin@example.com");
        var reset = service.Set("DbDataSync:State:Engine", before.DefaultValue!, author)!;

        Assert.Equal("Sqlite", reset.Value);
        Assert.Equal("Sqlite", DbDataSyncConfigFile.Read(_repoRoot)["DbDataSync:State:Engine"]);
        // Already at the default — nothing left to reset.
        Assert.False(reset.CanReset);
    }

    /// <summary>A key whose default is contextual (derived from the machine or working directory, not a
    /// fixed literal) has nothing for Reset to write — so it stays hidden rather than resetting to a
    /// value nobody actually chose.</summary>
    [Fact]
    public void AFileSourcedKeyWithNoFixedDefault_CanNeverBeReset()
    {
        DbDataSyncConfigFile.SetValue(_repoRoot, "DbDataSync", "State:ConnectionString", "Server=sql01;Database=DbDataSyncState;");
        var service = Build(ConfigurationWithFile(DbDataSyncConfigFile.Read(_repoRoot)));

        var entry = service.Get(StateConnectionStringKey)!;
        Assert.Null(entry.DefaultValue);
        Assert.False(entry.CanReset);
    }

    /// <summary>The whole point of RunningValue: it's frozen at ApiOptions construction, so a file
    /// write made after the service was built — the same thing a save through this screen does — moves
    /// Value without moving RunningValue. An admin can queue several such edits, and each one is
    /// independently visible as "configured, but not running yet" until a restart rebuilds ApiOptions.</summary>
    [Fact]
    public void EditingAFileSourcedKey_MovesValue_ButNotTheFrozenRunningValue()
    {
        DbDataSyncConfigFile.SetValue(_repoRoot, "DbDataSync", "State:Engine", "MsSql");
        var service = Build(ConfigurationWithFile(DbDataSyncConfigFile.Read(_repoRoot)));

        var before = service.Get("DbDataSync:State:Engine")!;
        Assert.Equal("file", before.Source);
        Assert.Equal("MsSql", before.Value);
        Assert.Equal("MsSql", before.RunningValue);

        var author = new GitAuthor("Test Admin", "admin@example.com");
        var after = service.Set("DbDataSync:State:Engine", "Postgres", author)!;

        Assert.Equal("Postgres", after.Value);
        // Still MsSql: ApiOptions was built once, from the configuration this service was constructed
        // with, and Set() never touches it — only a fresh process picks up the new file value.
        Assert.Equal("MsSql", after.RunningValue);
    }

    /// <summary>The full flow this exists for: override a non-file-sourced key (adopting its current
    /// value into the file changes nothing yet — the file now just says what was already running), then
    /// edit it again. Only that second edit actually queues a change RunningValue doesn't reflect until
    /// a restart.</summary>
    [Fact]
    public void OverridingThenEditingAnEnvVarSourcedKey_OnlyDivergesAfterTheSecondEdit()
    {
        Environment.SetEnvironmentVariable("DbDataSync__State__Engine", "MsSql");
        try
        {
            var service = Build(ConfigurationWithFile());
            var author = new GitAuthor("Test Admin", "admin@example.com");

            var entry = service.Get("DbDataSync:State:Engine")!;
            Assert.Equal("environment variable", entry.Source);
            Assert.Equal("MsSql", entry.Value);
            Assert.Equal("MsSql", entry.RunningValue);
            Assert.True(entry.CanAdopt);

            var adopted = service.Set("DbDataSync:State:Engine", entry.Value!, author)!;
            Assert.Equal("file", adopted.Source);
            Assert.Equal("MsSql", adopted.Value);
            Assert.Equal("MsSql", adopted.RunningValue);
            Assert.Equal(adopted.Value, adopted.RunningValue); // Just adopted — nothing to apply yet.

            var edited = service.Set("DbDataSync:State:Engine", "Postgres", author)!;
            Assert.Equal("Postgres", edited.Value);
            Assert.Equal("MsSql", edited.RunningValue); // Still frozen — this is the queued, unapplied change.
        }
        finally
        {
            Environment.SetEnvironmentVariable("DbDataSync__State__Engine", null);
        }
    }

    [Fact]
    public void AnEnvVarSourcedKey_IsReadOnly_ButCanBeAdopted()
    {
        Environment.SetEnvironmentVariable("DbDataSync__App__Url", "http://from-env/");
        try
        {
            var service = Build(ConfigurationWithFile());

            var entry = service.Get(UrlKey)!;
            Assert.Equal("environment variable", entry.Source);
            Assert.Equal("http://from-env/", entry.Value);
            Assert.False(entry.Editable);
            Assert.True(entry.CanAdopt);

            var author = new GitAuthor("Test Admin", "admin@example.com");
            var adopted = service.Set(UrlKey, entry.Value!, author);

            Assert.NotNull(adopted);
            Assert.Equal("http://from-env/", DbDataSyncConfigFile.Read(_repoRoot)[UrlKey]);
        }
        finally
        {
            Environment.SetEnvironmentVariable("DbDataSync__App__Url", null);
        }
    }

    [Fact]
    public void ACommandLineSourcedKey_IsLabeledCommandLine()
    {
        var builder = new ConfigurationBuilder();
        builder.AddCommandLine(["--DbDataSync:App:Url", "http://from-cli/"]);
        var service = Build(builder.Build());

        var entry = service.Get(UrlKey)!;

        Assert.Equal("command line", entry.Source);
        Assert.Equal("http://from-cli/", entry.Value);
    }

    /// <summary>Phase 164's replacement for the old blanket Auth:Disabled: two independent, role-scoped
    /// modes, both genuinely file-writable now (unlike the flag they replaced).</summary>
    [Fact]
    public void AuthNetworkKeys_AreFileWritable_AndDefaultToDisabled()
    {
        var service = Build(ConfigurationWithFile());

        var admin = service.Get("DbDataSync:Auth:Network:Admin")!;
        var viewer = service.Get("DbDataSync:Auth:Network:Viewer")!;
        Assert.Equal("disabled", admin.Value);
        Assert.Equal("disabled", viewer.Value);
        Assert.NotNull(admin.Caution);
        Assert.NotNull(viewer.Caution);

        var author = new GitAuthor("Test Admin", "admin@example.com");
        var written = service.Set("DbDataSync:Auth:Network:Admin", "loopback", author);

        Assert.NotNull(written);
        Assert.Equal("loopback", DbDataSyncConfigFile.Read(_repoRoot)["DbDataSync:Auth:Network:Admin"]);
    }

    [Fact]
    public void AnUnknownKey_ReturnsNull() => Assert.Null(Build().Get("DbDataSync:NotARealKey"));

    /// <summary>File-sourced never masks — phase 79 guarantees the file itself never carries one.</summary>
    [Fact]
    public void AFileSourcedStateConnectionString_IsNeverMasked()
    {
        DbDataSyncConfigFile.SetValue(_repoRoot, "DbDataSync", "State:ConnectionString", "Server=sql01;Database=DbDataSyncState;");
        var service = Build(ConfigurationWithFile(DbDataSyncConfigFile.Read(_repoRoot)));

        var entry = service.Get(StateConnectionStringKey)!;

        Assert.Equal("file", entry.Source);
        Assert.False(entry.Masked);
        Assert.Equal("Server=sql01;Database=DbDataSyncState;", entry.Value);
    }

    /// <summary>An env-var State:ConnectionString carrying a raw password is withheld entirely — never
    /// sent as Value, and not adoptable (there is nothing safe to write).</summary>
    [Fact]
    public void AnEnvVarStateConnectionStringWithAPassword_IsMaskedAndNotAdoptable()
    {
        Environment.SetEnvironmentVariable("DbDataSync__State__ConnectionString", "Server=sql01;Password=hunter2;");
        try
        {
            var service = Build(ConfigurationWithFile());

            var entry = service.Get(StateConnectionStringKey)!;

            Assert.Equal("environment variable", entry.Source);
            Assert.True(entry.Masked);
            Assert.Null(entry.Value);
            Assert.False(entry.CanAdopt);
        }
        finally
        {
            Environment.SetEnvironmentVariable("DbDataSync__State__ConnectionString", null);
        }
    }

    /// <summary>An env-var State:ConnectionString with no credential in it is safe to show and adopt —
    /// the password, if any, always lives in the secret store, never in this value.</summary>
    [Fact]
    public void AnEnvVarStateConnectionStringWithoutAPassword_IsShownAndAdoptable()
    {
        Environment.SetEnvironmentVariable("DbDataSync__State__ConnectionString", "Server=sql01;Database=DbDataSyncState;");
        try
        {
            var service = Build(ConfigurationWithFile());

            var entry = service.Get(StateConnectionStringKey)!;

            Assert.False(entry.Masked);
            Assert.Equal("Server=sql01;Database=DbDataSyncState;", entry.Value);
            Assert.True(entry.CanAdopt);
        }
        finally
        {
            Environment.SetEnvironmentVariable("DbDataSync__State__ConnectionString", null);
        }
    }

    [Fact]
    public void TheSecretEndpointHelper_StoresUnderTheFixedRef_AndOnlyForStateConnectionString()
    {
        var secrets = SecretStore.ForProviders([new InMemorySecretProvider()]);
        var apiOptions = ApiOptions.FromConfiguration(ConfigurationWithFile());
        var service = new AdminConfigService(
            ConfigurationWithFile(),
            apiOptions,
            AuthOptions.FromConfiguration(ConfigurationWithFile()),
            PasskeyOptions.FromConfiguration(ConfigurationWithFile(), apiOptions),
            new GitCommitService(_repoRoot),
            secrets,
            new RestartRequiredState(apiOptions));

        Assert.True(service.SetStateConnectionSecret(StateConnectionStringKey, "hunter2"));
        Assert.True(secrets.TryResolve(DbDataSync.Core.Secrets.SecretRefs.ForAppSetting("stateConnectionString"), out var stored));
        Assert.Equal("hunter2", stored);

        Assert.False(service.SetStateConnectionSecret(UrlKey, "nope"));
    }

    /// <summary>
    /// Phase 93: the same helper, through a store built with the "DbDataSync" prefix — as the real
    /// composition root builds it — end to end against the new naming, not just against an unprefixed
    /// test double.
    /// </summary>
    [Fact]
    public void TheSecretEndpointHelper_AgainstADbDataSyncPrefixedStore_ResolvesUnderTheNewPrefix()
    {
        var secrets = SecretStore.ForProviders("DbDataSync", [new InMemorySecretProvider()]);
        var apiOptions = ApiOptions.FromConfiguration(ConfigurationWithFile());
        var service = new AdminConfigService(
            ConfigurationWithFile(),
            apiOptions,
            AuthOptions.FromConfiguration(ConfigurationWithFile()),
            PasskeyOptions.FromConfiguration(ConfigurationWithFile(), apiOptions),
            new GitCommitService(_repoRoot),
            secrets,
            new RestartRequiredState(apiOptions));

        var secretRef = DbDataSync.Core.Secrets.SecretRefs.ForAppSetting("stateConnectionString");
        Assert.True(service.SetStateConnectionSecret(StateConnectionStringKey, "hunter2"));
        Assert.Equal("hunter2", secrets.Resolve(secretRef));
        Assert.Equal("DBDATASYNC_SECRET_DBDATASYNC_CONFIG_STATECONNECTIONSTRING", secrets.EnvName(secretRef));
    }
}
