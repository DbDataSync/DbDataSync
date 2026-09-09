using DbDataSync.Core.Config;
using DbDataSync.Core.Secrets;
using Microsoft.Data.SqlClient;

namespace DbDataSync.Cli.Tests;

/// <summary>
/// The concrete regression phase 79 exists to fix: before it, <c>InviteCommand</c> called
/// <c>new StateDatabase(stateDb)</c> unconditionally — no branch for <c>DbDataSync:StateEngine</c> being
/// anything but SQLite — so an admin locked out of an MsSql-backed deployment had no way to mint a
/// recovery invite. This proves <c>dbdatasync invite</c> now works end to end against a
/// <c>StateEngine: MsSql</c>-configured repo, secret included, and that the secret it uses is the one
/// <c>dbdatasync config secret set</c> — the same command an admin would actually run — just wrote.
/// <para>
/// Needs a real SQL Server reachable at <c>DBDATASYNC_TEST_MSSQL_SERVER</c> (default: the same local
/// Docker container every other MsSql-backed test in this repo uses) — the constructor issues
/// <c>CREATE DATABASE</c> for every test. Tagged <c>Category=Integration</c> so CI's no-container
/// <c>dotnet</c> job skips it and the <c>dotnet-integration</c> job (which stands one up) runs it,
/// the same split every other MsSql-backed suite here uses.
/// </para>
/// </summary>
[Trait("Category", "Integration")]
public sealed class InviteCommandTests : IDisposable
{
    private const string Password = "DbDataSync_Test_Pw1";

    private static readonly string ServerConnectionString =
        Environment.GetEnvironmentVariable("DBDATASYNC_TEST_MSSQL_SERVER")
        ?? $"Data Source=localhost,14330;User ID=sa;Password={Password};TrustServerCertificate=True";

    private readonly string _root = Directory.CreateTempSubdirectory("dbdatasync-invite-tests-").FullName;
    private readonly string _databaseName = $"DbDataSyncInviteTest_{Guid.NewGuid():N}";
    private readonly string _secretRef = SecretRefs.ForAppSetting("stateConnectionString");

    public InviteCommandTests()
    {
        Execute(ServerConnectionString, $"CREATE DATABASE [{_databaseName}];");
    }

    public void Dispose()
    {
        // Written by the CLI command under test, so clean it up the same way (`secret remove`), not by
        // reaching into SecretStore directly.
        SecretCommand.Run(["remove", _secretRef]);

        Directory.Delete(_root, recursive: true);

        try
        {
            Execute(ServerConnectionString, $"ALTER DATABASE [{_databaseName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;");
            Execute(ServerConnectionString, $"DROP DATABASE [{_databaseName}];");
        }
        catch (Exception)
        {
            // A scratch database left behind is untidy, not a reason to fail an otherwise-passing run —
            // the name carries a GUID, so nothing collides with a future run.
        }
    }

    [Fact]
    public void Invite_AgainstAnMsSqlConfiguredRepo_Succeeds()
    {
        var builder = new SqlConnectionStringBuilder(ServerConnectionString) { InitialCatalog = _databaseName };
        // The connection string DbDataSync itself is configured with never carries a password —
        // Remove, not `Password = ""`, which leaves a bare `Password=` in the output that
        // ConfigValidation.RejectEmbeddedCredential still refuses.
        builder.Remove("Password");
        var connectionString = builder.ConnectionString.TrimEnd(';');

        DbDataSyncConfigFile.SetValue(_root, "DbDataSync", "StateEngine", "MsSql");
        DbDataSyncConfigFile.SetValue(_root, "DbDataSync", "StateConnectionString", connectionString);

        // The same command an admin locked out of this deployment would actually run.
        Assert.Equal(0, SecretCommand.Run(["set", _secretRef, Password]));

        var (exitCode, output) = RunInvite(["--repo", _root, "--role", "Admin"]);

        Assert.Equal(0, exitCode);
        Assert.Contains("/invite#", output);
    }

    private static (int ExitCode, string Output) RunInvite(string[] args)
    {
        var originalOut = Console.Out;
        var originalErr = Console.Error;
        using var output = new StringWriter();
        Console.SetOut(output);
        Console.SetError(output);
        try
        {
            var exitCode = InviteCommand.Run(args);
            return (exitCode, output.ToString());
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetError(originalErr);
        }
    }

    private static void Execute(string serverConnectionString, string sql)
    {
        using var connection = new SqlConnection(serverConnectionString);
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }
}
