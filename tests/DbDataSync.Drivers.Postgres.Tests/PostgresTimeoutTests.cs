using DbDataSync.Core.Config;
using DbDataSync.Core.Sql;
using Npgsql;
using Xunit;

namespace DbDataSync.Drivers.Postgres.Tests;

/// <summary>The Postgres half of <c>MsSqlTimeoutTests</c>. Npgsql spells connect timeout
/// <c>Timeout</c>; everything else is the same claim.</summary>
public sealed class PostgresTimeoutTests
{
    private static ConnectionConfig Config(int? connect = null, int? command = null) => new()
    {
        Name = "timeouts",
        DriverType = ConnectionDriverType.Postgres,
        Host = "localhost",
        Port = 5432,
        Database = "probe",
        AuthMode = AuthMode.None,
        ConnectTimeoutSeconds = connect,
        CommandTimeoutSeconds = command,
    };

    private static NpgsqlConnectionStringBuilder StringFor(ConnectionConfig config) =>
        new(new PostgresDriver().CreateConnection(config, credential: null).ConnectionString);

    [Fact]
    public void ConnectTimeout_UnsetLandsOnTheDefault()
    {
        Assert.Equal(ConnectionTimeouts.DefaultConnectSeconds, StringFor(Config()).Timeout);
        Assert.Equal(30, StringFor(Config()).Timeout);
    }

    [Fact]
    public void ConnectTimeout_ZeroMeansUnlimitedAndIsPassedThroughUntranslated()
    {
        Assert.Equal(0, StringFor(Config(connect: 0)).Timeout);
    }

    [Fact]
    public void ConnectTimeout_PositiveValueIsUsedExactly()
    {
        Assert.Equal(90, StringFor(Config(connect: 90)).Timeout);
    }

    [Fact]
    public void ConnectTimeout_AnOperatorsOwnConnectionStringWins()
    {
        var config = Config();
        config.Host = null;
        config.Port = null;
        config.ConnectionString = "Host=localhost;Timeout=5";

        Assert.Equal(5, StringFor(config).Timeout);
    }

    [Fact]
    public void ConnectTimeout_TheSettingBeatsTheConnectionString()
    {
        var config = Config(connect: 45);
        config.Host = null;
        config.Port = null;
        config.ConnectionString = "Host=localhost;Timeout=5";

        Assert.Equal(45, StringFor(config).Timeout);
    }

    [Theory]
    [InlineData(null, ConnectionTimeouts.DefaultCommandSeconds)]
    [InlineData(0, 0)]
    [InlineData(120, 120)]
    public void CommandTimeout_ReachesACommandRaisedThroughTheChokepoint(int? configured, int expected)
    {
        using var connection = new PostgresDriver().CreateConnection(Config(command: configured), null);

        using var command = connection.CreateTimedCommand();

        Assert.Equal(expected, command.CommandTimeout);
    }
}
