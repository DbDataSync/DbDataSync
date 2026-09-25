using System.Data;
using System.Data.Common;
using DbDataSync.Core.Config;

namespace DbDataSync.Drivers.Generic.Tests;

/// <summary>
/// <see cref="GenericDriverSpec.FixedConnectionStringProperties"/> — the motivating case is an
/// ODBC-backed descriptor (<c>library: system-data-odbc</c>) baking in <c>Driver={...}</c>, but the
/// mechanism itself is engine-neutral, so these tests exercise it through a stub provider rather than a
/// real one.
/// <para>
/// Assertions read the result back through a fresh <see cref="DbConnectionStringBuilder"/> rather than
/// matching raw text: the builder itself quotes a value containing <c>{</c>/<c>}</c> (<c>Driver="{ODBC
/// Driver 18 for SQL Server}"</c>) and lower-cases keys on its own, standard, correct behaviour that has
/// nothing to do with what this feature is actually responsible for.
/// </para>
/// </summary>
public sealed class GenericDriverFixedConnectionStringPropertiesTests
{
    private sealed class StubDbConnection : DbConnection
    {
        public override string ConnectionString { get; set; } = "";
        public override string Database => "";
        public override string DataSource => "";
        public override string ServerVersion => "";
        public override ConnectionState State => ConnectionState.Closed;
        public override void ChangeDatabase(string databaseName) { }
        public override void Close() { }
        public override void Open() { }
        protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel) => throw new NotSupportedException();
        protected override DbCommand CreateDbCommand() => throw new NotSupportedException();
    }

    private sealed class StubDbProviderFactory : DbProviderFactory
    {
        public override DbConnection CreateConnection() => new StubDbConnection();
    }

    private static GenericDriverSpec Spec(IReadOnlyDictionary<string, string>? fixedProperties = null) =>
        new(
            "test-odbc", BracketDialect.Instance, new StubDbProviderFactory(), new InformationSchemaQueries(BracketDialect.Instance),
            Readers: [], Staging: [], Writers: [],
            ConnectionStringKeys: new GenericConnectionStringKeys(Host: "Server", Username: "UID", Password: "PWD"),
            DefaultDatabase: "master",
            FixedConnectionStringProperties: fixedProperties);

    private static ConnectionConfig Connection(string? connectionString = null) => new()
    {
        Name = "c", DriverType = "test-odbc", Host = "myserver", Database = "mydb",
        AuthMode = AuthMode.SqlAuth, UserId = "sa", ConnectionString = connectionString,
    };

    /// <summary>Reads a key back case-insensitively, the same way <see cref="DbConnectionStringBuilder"/>
    /// itself resolves one — the point of going through this rather than raw text.</summary>
    private static string? KeyOf(string connectionString, string key) =>
        new DbConnectionStringBuilder { ConnectionString = connectionString }.TryGetValue(key, out var value)
            ? value.ToString()
            : null;

    [Fact]
    public void FixedProperties_AreIncludedInTheAssembledConnectionString()
    {
        var driver = new GenericDriver(Spec(new Dictionary<string, string> { ["Driver"] = "{ODBC Driver 18 for SQL Server}" }));

        using var connection = driver.CreateConnection(Connection(), "pw");

        Assert.Equal("{ODBC Driver 18 for SQL Server}", KeyOf(connection.ConnectionString, "Driver"));
        Assert.Equal("myserver", KeyOf(connection.ConnectionString, "Server"));
        Assert.Equal("sa", KeyOf(connection.ConnectionString, "UID"));
        Assert.Equal("pw", KeyOf(connection.ConnectionString, "PWD"));
    }

    [Fact]
    public void NoFixedProperties_OmitsThemEntirely_AndBehavesExactlyAsBefore()
    {
        var driver = new GenericDriver(Spec());

        using var connection = driver.CreateConnection(Connection(), "pw");

        Assert.Null(KeyOf(connection.ConnectionString, "Driver"));
        Assert.Equal("myserver", KeyOf(connection.ConnectionString, "Server"));
    }

    [Fact]
    public void AnOperatorsOwnConnectionProperty_OverridesAFixedPropertysKey()
    {
        var spec = Spec(new Dictionary<string, string> { ["Driver"] = "{ODBC Driver 18 for SQL Server}" });
        var driver = new GenericDriver(spec);
        var connection = Connection();
        connection.Properties["Driver"] = "{ODBC Driver 17 for SQL Server}";

        using var built = driver.CreateConnection(connection, "pw");

        Assert.Equal("{ODBC Driver 17 for SQL Server}", KeyOf(built.ConnectionString, "Driver"));
    }

    [Fact]
    public void AHandSuppliedCompleteConnectionString_ReplacesTheFixedPropertiesEntirely()
    {
        // Matches JdbcGenericDriver's own "a hand-specified connection owns the whole string" precedent
        // — an operator bypassing the form fields is expected to include everything themselves.
        var spec = Spec(new Dictionary<string, string> { ["Driver"] = "{ODBC Driver 18 for SQL Server}" });
        var driver = new GenericDriver(spec);

        using var built = driver.CreateConnection(Connection("Driver={My Own Driver};Server=elsewhere;Database=mydb"), "pw");

        Assert.Equal("{My Own Driver}", KeyOf(built.ConnectionString, "Driver"));
        Assert.Equal("elsewhere", KeyOf(built.ConnectionString, "Server"));
    }

    [Fact]
    public void PreviewConnection_AlsoIncludesTheFixedProperties_WithTheCredentialMasked()
    {
        var driver = new GenericDriver(Spec(new Dictionary<string, string> { ["Driver"] = "{ODBC Driver 18 for SQL Server}" }));

        var preview = driver.PreviewConnection(Connection());

        Assert.Equal("{ODBC Driver 18 for SQL Server}", KeyOf(preview.ConnectionString, "Driver"));
        Assert.Equal("••••••", KeyOf(preview.ConnectionString, "PWD")); // the credential, never the username, is masked
    }
}
