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

    // libgit2 writes its object files read-only; plain Directory.Delete refuses to remove a read-only
    // file on Windows. Same workaround as tests/DbDataSync.Cli.Tests/GitTempDirectory.cs (not shared
    // across projects — small enough not to be worth a cross-project dependency for).
    public void Dispose()
    {
        foreach (var file in Directory.EnumerateFiles(_repoRoot, "*", SearchOption.AllDirectories))
            File.SetAttributes(file, FileAttributes.Normal);

        Directory.Delete(_repoRoot, recursive: true);
    }

    private const string UrlKey = "DbDataSync:Url";
    private const string StateConnectionStringKey = "DbDataSync:StateConnectionString";

    private AdminConfigService Build(IConfiguration? configuration = null)
    {
        configuration ??= new ConfigurationBuilder().Build();
        var apiOptions = ApiOptions.FromConfiguration(configuration);
        return new AdminConfigService(
            configuration,
            apiOptions,
            AuthOptions.FromConfiguration(configuration),
            PasskeyOptions.FromConfiguration(configuration),
            new GitCommitService(_repoRoot),
            SecretStore.ForProviders([new InMemorySecretProvider()]),
            new RestartRequiredState(apiOptions));
    }

    /// <summary>The same precedence InsertConfigFile establishes: the file first, environment variables
    /// after (so either can still override it), matching docs/configuration.md's own documented ordering. Always
    /// carries DbDataSync:RepoRoot pointed at this test's own temp directory — otherwise ApiOptions
    /// defaults it to "&lt;cwd&gt;/dbdatasync-repo", which is not where this test's own SetValue/Read
    /// calls (against _repoRoot) are looking.</summary>
    private IConfiguration ConfigurationWithFile(IReadOnlyDictionary<string, string?>? fileData = null)
    {
        var builder = new ConfigurationBuilder();
        if (fileData is not null)
            builder.Add(new DbDataSyncConfigFileSource { InitialData = fileData });
        builder.AddInMemoryCollection(new Dictionary<string, string?> { ["DbDataSync:RepoRoot"] = _repoRoot });
        builder.AddEnvironmentVariables();
        return builder.Build();
    }

    [Fact]
    public void NothingConfigures_TheKey_SourceIsDefault()
    {
        var service = Build(ConfigurationWithFile());

        var entry = service.Get(UrlKey)!;

        Assert.Equal("default", entry.Source);
        Assert.Null(entry.Value);
        Assert.False(entry.Editable);
        // Nothing to adopt when there is genuinely no value.
        Assert.False(entry.CanAdopt);
    }

    [Fact]
    public void ADefaultBackedByApiOptions_ShowsTheComputedDefault_NotNull()
    {
        var service = Build(ConfigurationWithFile());

        var entry = service.Get("DbDataSync:StateEngine")!;

        Assert.Equal("default", entry.Source);
        Assert.Equal("Sqlite", entry.Value);
    }

    [Fact]
    public void ADurationOrCountKey_ReportsItsUnit_AndAPathOrEngineKeyReportsNone()
    {
        var service = Build(ConfigurationWithFile());

        Assert.Equal("days", service.Get("DbDataSync:RunRetentionDays")!.Unit);
        Assert.Equal("runs", service.Get("DbDataSync:RunRetentionMaxPerMapping")!.Unit);
        Assert.Equal("minutes", service.Get("DbDataSync:RunPruningIntervalMinutes")!.Unit);
        Assert.Equal("days", service.Get("DbDataSync:ChangeCheckRetentionDays")!.Unit);
        // Not a plain magnitude — an engine name and a filesystem path, not a count of anything.
        Assert.Null(service.Get("DbDataSync:StateEngine")!.Unit);
        Assert.Null(service.Get(UrlKey)!.Unit);
    }

    [Fact]
    public void AFileSourcedKey_IsEditable_AndSetWritesAndCommitsIt()
    {
        DbDataSyncConfigFile.SetValue(_repoRoot, "DbDataSync", "Url", "http://initial/");
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
        Assert.Contains("DbDataSync:Url", repo.Head.Tip.Message);
    }

    /// <summary>Reset is Adopt's inverse: putting the factory default back into the file rather than
    /// taking a non-file value out of it. Offered only while there's something to undo, and Set(key,
    /// DefaultValue) is genuinely all it takes — no separate endpoint or code path.</summary>
    [Fact]
    public void AFileSourcedKeyAwayFromItsDefault_CanBeReset_BackToTheApplicationDefault()
    {
        DbDataSyncConfigFile.SetValue(_repoRoot, "DbDataSync", "StateEngine", "Postgres");
        var service = Build(ConfigurationWithFile(DbDataSyncConfigFile.Read(_repoRoot)));

        var before = service.Get("DbDataSync:StateEngine")!;
        Assert.Equal("Postgres", before.Value);
        Assert.Equal("Sqlite", before.DefaultValue);
        Assert.True(before.CanReset);

        var author = new GitAuthor("Test Admin", "admin@example.com");
        var reset = service.Set("DbDataSync:StateEngine", before.DefaultValue!, author)!;

        Assert.Equal("Sqlite", reset.Value);
        Assert.Equal("Sqlite", DbDataSyncConfigFile.Read(_repoRoot)["DbDataSync:StateEngine"]);
        // Already at the default — nothing left to reset.
        Assert.False(reset.CanReset);
    }

    /// <summary>A key whose default is contextual (derived from the machine or working directory, not a
    /// fixed literal) has nothing for Reset to write — so it stays hidden rather than resetting to a
    /// value nobody actually chose.</summary>
    [Fact]
    public void AFileSourcedKeyWithNoFixedDefault_CanNeverBeReset()
    {
        DbDataSyncConfigFile.SetValue(_repoRoot, "DbDataSync", "Url", "http://initial/");
        var service = Build(ConfigurationWithFile(DbDataSyncConfigFile.Read(_repoRoot)));

        var entry = service.Get(UrlKey)!;
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
        DbDataSyncConfigFile.SetValue(_repoRoot, "DbDataSync", "StateEngine", "MsSql");
        var service = Build(ConfigurationWithFile(DbDataSyncConfigFile.Read(_repoRoot)));

        var before = service.Get("DbDataSync:StateEngine")!;
        Assert.Equal("file", before.Source);
        Assert.Equal("MsSql", before.Value);
        Assert.Equal("MsSql", before.RunningValue);

        var author = new GitAuthor("Test Admin", "admin@example.com");
        var after = service.Set("DbDataSync:StateEngine", "Postgres", author)!;

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
        Environment.SetEnvironmentVariable("DbDataSync__StateEngine", "MsSql");
        try
        {
            var service = Build(ConfigurationWithFile());
            var author = new GitAuthor("Test Admin", "admin@example.com");

            var entry = service.Get("DbDataSync:StateEngine")!;
            Assert.Equal("environment variable", entry.Source);
            Assert.Equal("MsSql", entry.Value);
            Assert.Equal("MsSql", entry.RunningValue);
            Assert.True(entry.CanAdopt);

            var adopted = service.Set("DbDataSync:StateEngine", entry.Value!, author)!;
            Assert.Equal("file", adopted.Source);
            Assert.Equal("MsSql", adopted.Value);
            Assert.Equal("MsSql", adopted.RunningValue);
            Assert.Equal(adopted.Value, adopted.RunningValue); // Just adopted — nothing to apply yet.

            var edited = service.Set("DbDataSync:StateEngine", "Postgres", author)!;
            Assert.Equal("Postgres", edited.Value);
            Assert.Equal("MsSql", edited.RunningValue); // Still frozen — this is the queued, unapplied change.
        }
        finally
        {
            Environment.SetEnvironmentVariable("DbDataSync__StateEngine", null);
        }
    }

    [Fact]
    public void AnEnvVarSourcedKey_IsReadOnly_ButCanBeAdopted()
    {
        Environment.SetEnvironmentVariable("DbDataSync__Url", "http://from-env/");
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
            Environment.SetEnvironmentVariable("DbDataSync__Url", null);
        }
    }

    [Fact]
    public void ACommandLineSourcedKey_IsLabeledCommandLine()
    {
        var builder = new ConfigurationBuilder();
        builder.AddCommandLine(["--DbDataSync:Url", "http://from-cli/"]);
        var service = Build(builder.Build());

        var entry = service.Get(UrlKey)!;

        Assert.Equal("command line", entry.Source);
        Assert.Equal("http://from-cli/", entry.Value);
    }

    [Fact]
    public void ANestedAuthKey_IsNeverWritable_EvenIfItLooksFileSourced()
    {
        var service = Build(ConfigurationWithFile());

        var entry = service.Get("DbDataSync:Auth:Disabled")!;
        Assert.False(entry.Editable);
        Assert.False(entry.CanAdopt);

        var author = new GitAuthor("Test Admin", "admin@example.com");
        Assert.Null(service.Set("DbDataSync:Auth:Disabled", "true", author));
    }

    [Fact]
    public void AnUnknownKey_ReturnsNull() => Assert.Null(Build().Get("DbDataSync:NotARealKey"));

    /// <summary>File-sourced never masks — phase 79 guarantees the file itself never carries one.</summary>
    [Fact]
    public void AFileSourcedStateConnectionString_IsNeverMasked()
    {
        DbDataSyncConfigFile.SetValue(_repoRoot, "DbDataSync", "StateConnectionString", "Server=sql01;Database=DbDataSyncState;");
        var service = Build(ConfigurationWithFile(DbDataSyncConfigFile.Read(_repoRoot)));

        var entry = service.Get(StateConnectionStringKey)!;

        Assert.Equal("file", entry.Source);
        Assert.False(entry.Masked);
        Assert.Equal("Server=sql01;Database=DbDataSyncState;", entry.Value);
    }

    /// <summary>An env-var StateConnectionString carrying a raw password is withheld entirely — never
    /// sent as Value, and not adoptable (there is nothing safe to write).</summary>
    [Fact]
    public void AnEnvVarStateConnectionStringWithAPassword_IsMaskedAndNotAdoptable()
    {
        Environment.SetEnvironmentVariable("DbDataSync__StateConnectionString", "Server=sql01;Password=hunter2;");
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
            Environment.SetEnvironmentVariable("DbDataSync__StateConnectionString", null);
        }
    }

    /// <summary>An env-var StateConnectionString with no credential in it is safe to show and adopt —
    /// the password, if any, always lives in the secret store, never in this value.</summary>
    [Fact]
    public void AnEnvVarStateConnectionStringWithoutAPassword_IsShownAndAdoptable()
    {
        Environment.SetEnvironmentVariable("DbDataSync__StateConnectionString", "Server=sql01;Database=DbDataSyncState;");
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
            Environment.SetEnvironmentVariable("DbDataSync__StateConnectionString", null);
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
            PasskeyOptions.FromConfiguration(ConfigurationWithFile()),
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
            PasskeyOptions.FromConfiguration(ConfigurationWithFile()),
            new GitCommitService(_repoRoot),
            secrets,
            new RestartRequiredState(apiOptions));

        var secretRef = DbDataSync.Core.Secrets.SecretRefs.ForAppSetting("stateConnectionString");
        Assert.True(service.SetStateConnectionSecret(StateConnectionStringKey, "hunter2"));
        Assert.Equal("hunter2", secrets.Resolve(secretRef));
        Assert.Equal("DBDATASYNC_SECRET_DBDATASYNC_CONFIG_STATECONNECTIONSTRING", secrets.EnvName(secretRef));
    }
}
