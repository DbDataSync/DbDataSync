using DataSync.Core.Config;
using DataSync.Core.Secrets;
using Microsoft.Data.SqlClient;

namespace DataSync.Cli.Tests;

/// <summary>
/// The concrete regression phase 79 exists to fix: before it, <c>InviteCommand</c> called
/// <c>new StateDatabase(stateDb)</c> unconditionally — no branch for <c>DataSync:StateEngine</c> being
/// anything but SQLite — so an admin locked out of an MsSql-backed deployment had no way to mint a
/// recovery invite. This proves <c>datasync invite</c> now works end to end against a
/// <c>StateEngine: MsSql</c>-configured repo, secret included, and that the secret it uses is the one
/// <c>datasync secret set</c> — the same command an admin would actually run — just wrote.
/// <para>
/// Needs a real SQL Server reachable at <c>DATASYNC_TEST_MSSQL_SERVER</c> (default: the same local
/// Docker container every other MsSql-backed test in this repo uses). If none is reachable in this
/// environment, this fails with a connection error rather than being skipped — the same as every other
/// MsSql-backed test here (see <c>MsSqlTestDatabase</c>, <c>StateEngineFixture</c>).
/// </para>
/// </summary>
public sealed class InviteCommandTests : IDisposable
{
    private const string Password = "DataSync_Test_Pw1";

    private static readonly string ServerConnectionString =
        Environment.GetEnvironmentVariable("DATASYNC_TEST_MSSQL_SERVER")
        ?? $"Data Source=localhost,14330;User ID=sa;Password={Password};TrustServerCertificate=True";

    private readonly string _root = Directory.CreateTempSubdirectory("datasync-invite-tests-").FullName;
    private readonly string _databaseName = $"DataSyncInviteTest_{Guid.NewGuid():N}";
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
        var connectionString = new SqlConnectionStringBuilder(ServerConnectionString)
        {
            InitialCatalog = _databaseName,
            Password = "", // the connection string DataSync itself is configured with never carries one
        }.ConnectionString.TrimEnd(';');

        DataSyncConfigFile.SetValue(_root, "DataSync", "StateEngine", "MsSql");
        DataSyncConfigFile.SetValue(_root, "DataSync", "StateConnectionString", connectionString);

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
