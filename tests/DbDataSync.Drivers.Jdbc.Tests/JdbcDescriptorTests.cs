using DbDataSync.Core.Config;
using DbDataSync.Drivers.Abstractions;
using DbDataSync.Drivers.Descriptor;
using DbDataSync.Drivers.Generic;
using DbDataSync.Libraries;
using Npgsql;

namespace DbDataSync.Drivers.Jdbc.Tests;

/// <summary>
/// Phase 168V's actual point, proven end to end: a <c>driver.yaml</c> naming
/// <c>JdbcGenericDriver</c> as its <c>base</c> round-trips through
/// <see cref="DriverDescriptorReader.BuildDriver"/> — the same dispatch
/// <c>DriverLoader.LoadDescriptorDrivers</c> uses at host startup — to a real driver that reads actual
/// rows from the live Postgres container, the same parity shape phase 165V's own
/// <see cref="JdbcReaderParityTests"/> already uses.
/// </summary>
[Trait("Category", "Integration")]
public sealed class JdbcDescriptorTests(JdbcTestDatabase db) : IClassFixture<JdbcTestDatabase>, IAsyncLifetime
{
    private readonly string _tableName = $"jdbc_descriptor_spike_{Guid.NewGuid():N}";
    private NpgsqlConnection _npgsql = null!;

    public async Task InitializeAsync()
    {
        _npgsql = db.OpenNpgsqlConnection();
        await ExecuteAsync(_npgsql, $"""
            CREATE TABLE public."{_tableName}" (id integer primary key, name varchar(50) not null);
            """);
        await ExecuteAsync(_npgsql, $"""
            INSERT INTO public."{_tableName}" VALUES (1, 'alice'), (2, 'bob');
            """);
    }

    public async Task DisposeAsync() => await _npgsql.DisposeAsync();

    private static async Task ExecuteAsync(NpgsqlConnection connection, string sql)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }

    [Fact]
    public async Task ADriverYamlNamingJdbcGenericDriver_BuildsAWorkingDriver()
    {
        // Phase 169V: driverJarPaths holds names inside <repo>/files/, not literal paths — a throwaway
        // repoRoot with the test project's own already-downloaded jar copied into files/ under a plain
        // name, the same shape a real operator upload would leave behind.
        var repoRoot = Path.Combine(Path.GetTempPath(), $"jdbc-descriptor-test-{Guid.NewGuid():N}");
        var filesDir = Path.Combine(repoRoot, "files");
        Directory.CreateDirectory(filesDir);
        File.Copy(Path.Combine(AppContext.BaseDirectory, "postgresql.jar"), Path.Combine(filesDir, "postgresql.jar"));

        const string yaml = """
            id: postgres-via-jdbc
            displayName: Postgres (via JDBC)
            library: ikvm
            base: DbDataSync.Drivers.Jdbc.JdbcGenericDriver, DbDataSync.Drivers.Jdbc
            jdbc:
              driverClass: org.postgresql.Driver
              driverJarPaths: [postgresql.jar]
            dialect:
              quoteIdentifier: doubleQuote
              parameterPrefix: "@"
              parameterNameIsBare: true
              rowLimit: limitOffset
              # supportsChangeDatabase: true (the default) — BatchReloadReader.ReadChangesAsync calls
              # dialect.UseDatabaseAsync unconditionally (a reader-level call, independent of
              # JdbcGenericDriver's own SwitchDatabaseAsync no-op, which only covers the driver's own
              # ListTablesAsync/ListColumnsAsync). For JDBC that resolves to JdbcConnection.ChangeDatabase
              # -> java.sql.Connection.setCatalog(...), which pgJDBC accepts — found by this test failing
              # first with supportsChangeDatabase: false, not assumed.
            typeMap:
              int4: Int32
              "varchar(n)": { kind: String, length: n, unicode: true }
            capabilities:
              readers: [Watermark, BatchReload]
              staging: []
              writers: []
            """;

        var descriptor = DriverDescriptorReader.Deserialize(yaml);
        // Never actually consulted for a JDBC base — see JdbcGenericDriver.FromDescriptor's own doc
        // comment — but BuildDriver's signature is shared with the ADO.NET path, which does need one.
        var libraries = new LibraryRegistry(Path.GetTempPath());

        var driver = DriverDescriptorReader.BuildDriver(descriptor, libraries, repoRoot);

        Assert.IsType<JdbcGenericDriver>(driver);
        Assert.Equal("postgres-via-jdbc", driver.DriverType);

        var config = new ConnectionConfig
        {
            Name = "jdbc-descriptor-test",
            DriverType = "postgres-via-jdbc",
            ConnectionString = $"{JdbcTestDatabase.JdbcUrl}{db.DatabaseName}",
            AuthMode = AuthMode.SqlAuth,
            UserId = "dbdatasync",
        };
        await using var connection = driver.CreateConnection(config, "DbDataSync_Test_Pw1");
        connection.Open();

        var reader = (BatchReloadReader)driver.Readers.Single(r => r.Kind == GenericDriverKinds.BatchReload);
        var source = new SourceTableRef { ConnectionName = "src", Database = db.DatabaseName, Schema = "public", Table = _tableName };
        var mappings = new List<ColumnMapping>
        {
            new() { SourceColumn = "id", TargetColumn = "id" },
            new() { SourceColumn = "name", TargetColumn = "name" },
        };

        var result = await reader.ReadChangesAsync(
            connection, source, null, ReadIntent.InitialLoad, mappings, "descriptor-test", [],
            new Dictionary<string, string>(), CancellationToken.None);

        var rows = new List<ChangeRow>();
        await foreach (var row in result.Rows)
            rows.Add(row);

        Assert.Equal(2, rows.Count);
        Assert.Contains(rows, r => (int)r["id"]! == 1 && (string)r["name"]! == "alice");
        Assert.Contains(rows, r => (int)r["id"]! == 2 && (string)r["name"]! == "bob");
    }
}
