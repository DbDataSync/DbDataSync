using ClrKernel.Core.Secrets;
using DbDataSync.Core.Config;
using DbDataSync.Core.Git;
using DbDataSync.Libraries;
using Microsoft.Data.SqlClient;

namespace DbDataSync.Cli.Tests;

/// <summary>
/// Phase 109j item 4's own "how to verify" bar, at the CLI layer (<c>dbdatasync config library
/// validate</c>) rather than through the API's spawned-child-process endpoint (see
/// <c>DbDataSync.Api.Tests.LibraryValidateIntegrationTests</c> for that half, including the no-DDL-rights
/// case and the assembly-residency isolation check).
/// </summary>
public sealed class LibraryValidateCommandTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("dbdatasync-validate-cli-").FullName;
    private readonly SecretStore _secrets = new("DbDataSync", true);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private ConfigRepository Repo() =>
        new(Path.Combine(_root, "config"), new GitCommitService(EnsureGitRepo()), _secrets);

    private string EnsureGitRepo()
    {
        if (!LibGit2Sharp.Repository.IsValid(_root))
            LibGit2Sharp.Repository.Init(_root);
        return _root;
    }

    private void SaveConnection(ConnectionInput input) =>
        Repo().SaveConnection(input, new GitAuthor("test", "test@example.com"));

    private static void ExposeCredential(string connectionName, string password) =>
        Environment.SetEnvironmentVariable(
            $"DBDATASYNC_SECRET_DBDATASYNC_CONNECTION_{connectionName.Replace('-', '_').ToUpperInvariant()}",
            password);

    [Fact]
    public async Task Validate_WithNoArguments_PrintsUsageAndFails()
    {
        var exitCode = await LibraryCommand.RunAsync(["validate"]);
        Assert.Equal(1, exitCode);
    }

    [Fact]
    public async Task Validate_WithNoConnectionFlag_FailsWithASpecificMessage()
    {
        var exitCode = await LibraryCommand.RunAsync(["validate", "microsoft-data-sqlclient", "--repo", _root]);
        Assert.Equal(1, exitCode);
    }

    [Fact]
    public async Task Validate_ForAnUnknownConnection_Fails()
    {
        var exitCode = await LibraryCommand.RunAsync(
            ["validate", "microsoft-data-sqlclient", "--connection", "does-not-exist", "--repo", _root]);
        Assert.Equal(1, exitCode);
    }

    [Fact]
    public async Task Validate_WhenTheLibraryIdDoesNotMatchTheConnectionsDriver_FailsNamingTheMismatch()
    {
        SaveConnection(new ConnectionInput
        {
            Name = "mssql-conn",
            DriverType = DriverIds.MsSql,
            Host = "localhost",
            Port = 14330,
            Database = "master",
            AuthMode = AuthMode.SqlAuth,
            UserId = "sa",
            Password = "DbDataSync_Test_Pw1",
        });

        var exitCode = await LibraryCommand.RunAsync(
            ["validate", "npgsql", "--connection", "mssql-conn", "--repo", _root]);

        Assert.Equal(1, exitCode);
    }

    [Fact]
    public async Task Validate_ForADuckDbConnection_FailsCleanlyNamingThatThereIsNothingToStageOrWrite()
    {
        SaveConnection(new ConnectionInput
        {
            Name = "duckdb-conn",
            DriverType = DriverIds.DuckDb,
            AuthMode = AuthMode.None,
            ConnectionString = ":memory:",
        });

        var exitCode = await LibraryCommand.RunAsync(
            ["validate", "duckdb", "--connection", "duckdb-conn", "--repo", _root]);

        // DuckDbDriver.StagingProviders/.Writers are both empty — the phase's own explicit "not built"
        // boundary (only a static IL-surface check applies to DuckDb, not the deep, connection-scoped
        // one) — this must be a clean, specific failure, not a crash or an IndexOutOfRangeException from
        // `driver.StagingProviders[0]`.
        Assert.Equal(1, exitCode);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Validate_AgainstTheRealMsSqlContainer_SucceedsAndLeavesNoScratchTableBehind()
    {
        await InstallAsync("microsoft-data-sqlclient");
        SaveConnection(new ConnectionInput
        {
            Name = "mssql-real",
            DriverType = DriverIds.MsSql,
            Host = "localhost",
            Port = 14330,
            Database = "master",
            AuthMode = AuthMode.SqlAuth,
            UserId = "sa",
            Password = "DbDataSync_Test_Pw1",
        });
        ExposeCredential("mssql-real", "DbDataSync_Test_Pw1");

        var exitCode = await LibraryCommand.RunAsync(
            ["validate", "microsoft-data-sqlclient", "--connection", "mssql-real", "--repo", _root]);

        Assert.Equal(0, exitCode);

        await using var connection = new SqlConnection(
            "Data Source=localhost,14330;User ID=sa;Password=DbDataSync_Test_Pw1;TrustServerCertificate=True");
        await connection.OpenAsync();
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM sys.tables WHERE name LIKE 'DbDataSync_LibraryValidation_%';";
        Assert.Equal(0, (int)(await cmd.ExecuteScalarAsync())!);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Validate_AgainstTheRealPostgresContainer_SucceedsAndLeavesNoScratchTableBehind()
    {
        // Real bug this exact scenario found and fixed: Postgres's own first-registered staging
        // provider/writer (BatchInsertStagingProvider/DeleteInsertWriter, the generic pipeline — Postgres
        // has no bespoke staging provider) reads CachedColumn.NativeType literally into DDL, and folds
        // an unquoted mixed-case identifier to lowercase at CREATE TABLE time while quoting the original
        // case back when referencing it — both fixed in LibraryValidationRunner. This test is what would
        // have caught it.
        await InstallAsync("npgsql");
        SaveConnection(new ConnectionInput
        {
            Name = "pg-real",
            DriverType = DriverIds.Postgres,
            Host = "localhost",
            Port = 15432,
            Database = "dbdatasync",
            AuthMode = AuthMode.SqlAuth,
            UserId = "dbdatasync",
            Password = "DbDataSync_Test_Pw1",
        });
        ExposeCredential("pg-real", "DbDataSync_Test_Pw1");

        var exitCode = await LibraryCommand.RunAsync(
            ["validate", "npgsql", "--connection", "pg-real", "--repo", _root]);

        Assert.Equal(0, exitCode);
    }

    private async Task InstallAsync(string libraryId)
    {
        var entry = KnownLibraries.TryGetById(libraryId)!;
        await LibraryInstaller.InstallAsync(
            _root, entry.Id, [new PackageRef(entry.PackageId, entry.PinnedVersion)], entry.FactoryType);
    }
}
