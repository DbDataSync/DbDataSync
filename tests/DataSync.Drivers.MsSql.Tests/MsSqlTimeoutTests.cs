using DataSync.Core.Config;
using DataSync.Core.Sql;
using Microsoft.Data.SqlClient;
using Xunit;

namespace DataSync.Drivers.MsSql.Tests;

/// <summary>
/// Connect timeout on the connection string, and command timeout on an actual <c>DbCommand</c> — the
/// second being the one that matters, because the connection-string layer proving correct says nothing
/// about whether a reader's query ever sees the value.
/// </summary>
public sealed class MsSqlTimeoutTests
{
    private static ConnectionConfig Config(int? connect = null, int? command = null) => new()
    {
        Name = "timeouts",
        DriverType = ConnectionDriverType.MsSql,
        Host = "localhost",
        Port = 1433,
        Database = "probe",
        AuthMode = AuthMode.None,
        ConnectTimeoutSeconds = connect,
        CommandTimeoutSeconds = command,
    };

    private static SqlConnectionStringBuilder StringFor(ConnectionConfig config) =>
        new(new MsSqlDriver().CreateConnection(config, credential: null).ConnectionString);

    [Fact]
    public void ConnectTimeout_UnsetLandsOnTheDefault()
    {
        Assert.Equal(ConnectionTimeouts.DefaultConnectSeconds, StringFor(Config()).ConnectTimeout);
        Assert.Equal(30, StringFor(Config()).ConnectTimeout);
    }

    [Fact]
    public void ConnectTimeout_ZeroMeansUnlimitedAndIsPassedThroughUntranslated()
    {
        // The point of the whole "no sentinel" decision: 0 in config is 0 in the connection string,
        // which is what SqlClient already documents as infinite.
        Assert.Equal(0, StringFor(Config(connect: 0)).ConnectTimeout);
    }

    [Fact]
    public void ConnectTimeout_PositiveValueIsUsedExactly()
    {
        Assert.Equal(90, StringFor(Config(connect: 90)).ConnectTimeout);
    }

    [Fact]
    public void ConnectTimeout_AnOperatorsOwnConnectionStringWins()
    {
        // Host mode is not the only way in. Somebody who wrote `Connect Timeout=5` themselves meant it,
        // and the default must not silently replace it — the same rule TrustServerCertificate follows.
        var config = Config();
        config.Host = null;
        config.Port = null;
        config.ConnectionString = "Data Source=localhost;Connect Timeout=5";

        Assert.Equal(5, StringFor(config).ConnectTimeout);
    }

    [Fact]
    public void ConnectTimeout_TheSettingBeatsTheConnectionString()
    {
        var config = Config(connect: 45);
        config.Host = null;
        config.Port = null;
        config.ConnectionString = "Data Source=localhost;Connect Timeout=5";

        Assert.Equal(45, StringFor(config).ConnectTimeout);
    }

    /// <summary>
    /// The test the phase actually turns on: a command raised the way every reader, writer, staging
    /// provider and provisioner raises one carries the configured timeout. Asserted on
    /// <see cref="System.Data.Common.DbCommand.CommandTimeout"/> itself, not on a connection string —
    /// there is no connection-string key for this, so the connection-string layer could be entirely
    /// correct while every query still ran at the provider's 30 seconds.
    /// </summary>
    [Theory]
    [InlineData(null, ConnectionTimeouts.DefaultCommandSeconds)]
    [InlineData(0, 0)]
    [InlineData(120, 120)]
    public void CommandTimeout_ReachesACommandRaisedThroughTheChokepoint(int? configured, int expected)
    {
        using var connection = new MsSqlDriver().CreateConnection(Config(command: configured), null);

        using var command = connection.CreateTimedCommand();

        Assert.Equal(expected, command.CommandTimeout);
    }

    [Fact]
    public void CommandTimeout_UnsetIsThirtyMinutesNotTheProvidersThirtySeconds()
    {
        using var connection = new MsSqlDriver().CreateConnection(Config(), null);
        using var command = connection.CreateTimedCommand();

        Assert.Equal(1800, command.CommandTimeout);
        Assert.NotEqual(30, command.CommandTimeout);
    }
}
