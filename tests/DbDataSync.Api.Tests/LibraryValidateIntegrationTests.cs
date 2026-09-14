using System.Net.Http.Json;
using System.Text.Json;
using DbDataSync.Core.Config;
using Microsoft.Data.SqlClient;
using Xunit;

namespace DbDataSync.Api.Tests;

/// <summary>
/// Phase 109j item 4, exercised end to end through the real HTTP endpoint
/// (<c>POST /api/connections/{name}/validate-library</c>), which spawns a real
/// <c>dotnet exec DbDataSync.Cli.dll config library validate ...</c> child process against the real
/// <c>dbdatasync-mssql-source</c> container already used throughout this repo's own integration suite —
/// not simulated, not mocked.
/// <para>
/// **Credential visibility across the process boundary**: <see cref="TestApiFactory"/> deliberately
/// swaps the API host's own <c>SecretStore</c> for an in-memory one (so tests never touch a real OS
/// keychain) — but the spawned child process builds its *own*, real <c>SecretStore</c>, which cannot
/// see that in-memory store. Rather than let the credential a real production deployment's OS
/// keyring/file store would carry silently be invisible here, this test sets the exact environment
/// variable <c>GET .../credential-source</c> already reports (production's own answer to "where does
/// this credential come from") on the current process before triggering validate — the child inherits
/// it, exactly as any spawned child process inherits its parent's environment.
/// </para>
/// </summary>
[Trait("Category", "Integration")]
public sealed class LibraryValidateIntegrationTests(TestApiFactory factory) : IClassFixture<TestApiFactory>
{
    private const string ServerConnectionString =
        "Data Source=localhost,14330;User ID=sa;Password=DbDataSync_Test_Pw1;TrustServerCertificate=True";

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private readonly HttpClient _client = factory.CreateClient();

    private sealed record CredentialSourceDto(string Store, string SecretRef, string EnvironmentVariable, bool RequiresCredential);
    private sealed record ValidationReportDto(bool Succeeded, string LibraryId, string Output);
    private sealed record TestReportDto(bool Succeeded, double ConnectMs, double ProbeMs, string? ServerVersion, string? Error, string? LibraryWarning);

    private async Task<string> CreateConnectionAsync(string userId, string password)
    {
        var name = $"test-conn-validate-{Guid.NewGuid():N}";
        var response = await _client.PutAsJsonAsync($"/api/connections/{name}", new ConnectionInput
        {
            Name = name,
            DriverType = DriverIds.MsSql,
            Host = "localhost",
            Port = 14330,
            Database = "master",
            AuthMode = AuthMode.SqlAuth,
            UserId = userId,
            Password = password,
        }, JsonOptions);
        response.EnsureSuccessStatusCode();

        // Forces the real production auto-install path (DriverConnectionFactory.EnsureLibraryInstalledAsync,
        // phase 109h) to seed `microsoft-data-sqlclient` into this class fixture's shared repo root —
        // deliberately, rather than relying on some other test in this class happening to have already
        // triggered it first. A plain "Test" needs only CONNECT rights, so this is safe to do
        // unconditionally, including for the read-only login used by the no-DDL-rights test below.
        (await _client.PostAsync($"/api/connections/{name}/test", null)).EnsureSuccessStatusCode();

        return name;
    }

    /// <summary>Makes the credential visible across the process boundary — see this class's own doc
    /// comment. Restored in the caller's <c>finally</c> so one test's env var never leaks into another.</summary>
    private async Task<string> ExposeCredentialToChildProcessAsync(string connectionName, string password)
    {
        var source = await _client.GetFromJsonAsync<CredentialSourceDto>(
            $"/api/connections/{connectionName}/credential-source", JsonOptions);
        var previous = Environment.GetEnvironmentVariable(source!.EnvironmentVariable);
        Environment.SetEnvironmentVariable(source.EnvironmentVariable, password);
        return source.EnvironmentVariable;
    }

    [Fact]
    public async Task Validate_AgainstARealServer_SucceedsAndLeavesNoScratchTableBehind()
    {
        var name = await CreateConnectionAsync("sa", "DbDataSync_Test_Pw1");
        var envVar = await ExposeCredentialToChildProcessAsync(name, "DbDataSync_Test_Pw1");
        try
        {
            var response = await _client.PostAsync($"/api/connections/{name}/validate-library", null);
            response.EnsureSuccessStatusCode();
            var report = await response.Content.ReadFromJsonAsync<ValidationReportDto>(JsonOptions);

            Assert.True(report!.Succeeded, report.Output);
            Assert.Equal("microsoft-data-sqlclient", report.LibraryId);
            Assert.Contains("validated", report.Output, StringComparison.OrdinalIgnoreCase);

            Assert.Equal(0, await CountScratchTablesAsync());
        }
        finally
        {
            Environment.SetEnvironmentVariable(envVar, null);
        }
    }

    [Fact]
    public async Task Validate_WithNoDdlRights_FailsNamingTheCreateTableStep_NotAGenericStagingError()
    {
        var (loginName, password) = await CreateReadOnlyLoginAsync();
        var name = await CreateConnectionAsync(loginName, password);
        var envVar = await ExposeCredentialToChildProcessAsync(name, password);
        try
        {
            var response = await _client.PostAsync($"/api/connections/{name}/validate-library", null);
            response.EnsureSuccessStatusCode();
            var report = await response.Content.ReadFromJsonAsync<ValidationReportDto>(JsonOptions);

            Assert.False(report!.Succeeded);
            Assert.True(report.Output.Contains("CREATE TABLE", StringComparison.OrdinalIgnoreCase), report.Output);
            Assert.Contains("scratch table", report.Output, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Environment.SetEnvironmentVariable(envVar, null);
            await DropLoginAsync(loginName);
        }
    }

    [Fact]
    public async Task Test_AgainstTheCurrentlyPinnedLibrary_ReportsNoLibraryWarning()
    {
        // The real, current end state: whatever `microsoft-data-sqlclient` version is actually pinned
        // and installed for this suite is expected to be clean — proving the static check's own
        // real-world "no false positives against real, current usage" bar through the same endpoint an
        // operator actually uses, not just through LibrarySurfaceChecker directly.
        var name = await CreateConnectionAsync("sa", "DbDataSync_Test_Pw1");

        var response = await _client.PostAsync($"/api/connections/{name}/test", null);
        response.EnsureSuccessStatusCode();
        var report = await response.Content.ReadFromJsonAsync<TestReportDto>(JsonOptions);

        Assert.True(report!.Succeeded, report.Error);
        Assert.Null(report.LibraryWarning);
    }

    private static async Task<long> CountScratchTablesAsync()
    {
        await using var connection = new SqlConnection(ServerConnectionString);
        await connection.OpenAsync();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM sys.tables WHERE name LIKE 'DbDataSync_LibraryValidation_%';";
        return (int)(await cmd.ExecuteScalarAsync())!;
    }

    /// <summary>A real SQL login with CONNECT SQL and db_datareader on <c>master</c> only — enough to
    /// open a connection and read, nothing that lets CREATE TABLE succeed. Dropped in the caller's
    /// <c>finally</c> via <see cref="DropLoginAsync"/>.</summary>
    private static async Task<(string LoginName, string Password)> CreateReadOnlyLoginAsync()
    {
        var loginName = $"dbdatasync_validate_ro_{Guid.NewGuid():N}"[..30];
        const string password = "DbDataSync_ReadOnly_Pw1!";

        await using var connection = new SqlConnection(ServerConnectionString);
        await connection.OpenAsync();
        await ExecuteAsync(connection, $"CREATE LOGIN [{loginName}] WITH PASSWORD = '{password}';");
        await ExecuteAsync(connection, $"CREATE USER [{loginName}] FOR LOGIN [{loginName}];");
        await ExecuteAsync(connection, $"ALTER ROLE db_datareader ADD MEMBER [{loginName}];");

        return (loginName, password);
    }

    private static async Task DropLoginAsync(string loginName)
    {
        // Both the API host's own pooled connection (from the "Test" call CreateConnectionAsync makes)
        // and the validate child process's connection can leave a physical connection open under this
        // login in ADO.NET's process-wide pool — DROP LOGIN refuses while any session is still logged
        // in. Clearing pools here (test cleanup only, never production code) is the standard fix.
        SqlConnection.ClearAllPools();
        await using var connection = new SqlConnection(ServerConnectionString);
        await connection.OpenAsync();
        await ExecuteAsync(connection, $"DROP USER IF EXISTS [{loginName}];");
        // Unlike DROP USER/TABLE, DROP LOGIN never got an IF EXISTS form — checked for real against the
        // actual container (SQL Server 2022) rather than assumed, since the syntax reads as though it
        // should exist. Guarded with sys.server_principals instead.
        await ExecuteAsync(connection,
            $"IF EXISTS (SELECT 1 FROM sys.server_principals WHERE name = '{loginName}') DROP LOGIN [{loginName}];");
    }

    private static async Task ExecuteAsync(SqlConnection connection, string sql)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }
}
