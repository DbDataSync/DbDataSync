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

    /// <summary>
    /// Phase 178N: before this, a driver.yaml's own <c>jdbc.urlTemplate</c>/<c>connectionStringKeys</c>
    /// were parsed by YamlDotNet but never read by <see cref="JdbcGenericDriver.FromDescriptor"/> — see
    /// follow-up-jdbc-url-template-unreachable-from-driver-yaml.md part 1. Proven end to end through
    /// <see cref="IConnectionPreviewer.PreviewConnection"/> rather than a live connect: the resolved JDBC
    /// URI shows the template actually placed {host}/{port} and the overridden username key, which only
    /// happens if the descriptor's values reached <see cref="JdbcDriverSpec"/>, not the class's
    /// hardcoded defaults.
    /// </summary>
    [Fact]
    public void ADriverYamlsUrlTemplateAndConnectionStringKeys_ReachTheBuiltDriver()
    {
        var repoRoot = Path.Combine(Path.GetTempPath(), $"jdbc-descriptor-url-template-test-{Guid.NewGuid():N}");
        var filesDir = Path.Combine(repoRoot, "files");
        Directory.CreateDirectory(filesDir);
        File.Copy(Path.Combine(AppContext.BaseDirectory, "postgresql.jar"), Path.Combine(filesDir, "postgresql.jar"));

        const string yaml = """
            id: postgres-via-jdbc-templated
            displayName: Postgres (via JDBC, templated)
            library: ikvm
            base: DbDataSync.Drivers.Jdbc.JdbcGenericDriver, DbDataSync.Drivers.Jdbc
            jdbc:
              driverClass: org.postgresql.Driver
              driverJarPaths: [postgresql.jar]
              urlTemplate: "jdbc:postgresql://{host}:{port}/{database}"
              connectionStringKeys:
                username: pguser
            dialect:
              quoteIdentifier: doubleQuote
              parameterPrefix: "@"
              parameterNameIsBare: true
              rowLimit: limitOffset
            capabilities:
              readers: [Watermark]
              staging: []
              writers: []
            """;

        var descriptor = DriverDescriptorReader.Deserialize(yaml);
        var libraries = new LibraryRegistry(Path.GetTempPath());
        var driver = (IConnectionPreviewer)DriverDescriptorReader.BuildDriver(descriptor, libraries, repoRoot);

        var config = new ConnectionConfig
        {
            Name = "jdbc-descriptor-url-template-test", DriverType = "postgres-via-jdbc-templated",
            Host = "localhost", Port = 15432, Database = db.DatabaseName,
            AuthMode = AuthMode.SqlAuth, UserId = "dbdatasync",
        };

        var preview = driver.PreviewConnection(config);

        Assert.Equal("jdbc:postgresql://localhost:15432/" + db.DatabaseName, preview.JdbcUri);
        Assert.Equal("dbdatasync", preview.Properties["pguser"]);
    }

    /// <summary>
    /// Regression: found while building phase 181N's validate/echo tool, not assumed. The first cut of
    /// phase 178N reused <c>DescriptorConnectionStringKeysYaml</c> (the ADO.NET dialect's own type)
    /// verbatim for the JDBC side too — that type's properties carry non-nullable, ADO.NET-flavoured C#
    /// defaults (<c>Host = "Host"</c>, etc.), so a yaml overriding only <em>one</em> field silently
    /// deserialized every other field at its ADO.NET default rather than leaving it unset. A template
    /// with no <c>{host}</c> token forces the fallback-to-property path to actually run for <c>host</c>,
    /// which is what exposes it — the sibling test above never does, because its template places
    /// <c>{host}</c> directly and never consults <c>keys.Host</c> as a property key at all.
    /// </summary>
    [Fact]
    public void APartialConnectionStringKeysOverride_LeavesUnmodeledFieldsAtJdbcsOwnDefaults()
    {
        var repoRoot = Path.Combine(Path.GetTempPath(), $"jdbc-descriptor-partial-keys-test-{Guid.NewGuid():N}");
        var filesDir = Path.Combine(repoRoot, "files");
        Directory.CreateDirectory(filesDir);
        File.Copy(Path.Combine(AppContext.BaseDirectory, "postgresql.jar"), Path.Combine(filesDir, "postgresql.jar"));

        const string yaml = """
            id: postgres-via-jdbc-partial-keys
            displayName: Postgres (via JDBC, partial key override)
            library: ikvm
            base: DbDataSync.Drivers.Jdbc.JdbcGenericDriver, DbDataSync.Drivers.Jdbc
            jdbc:
              driverClass: org.postgresql.Driver
              driverJarPaths: [postgresql.jar]
              urlTemplate: "jdbc:postgresql:///"
              connectionStringKeys:
                username: pguser
            dialect:
              quoteIdentifier: doubleQuote
              parameterPrefix: "@"
              parameterNameIsBare: true
              rowLimit: limitOffset
            capabilities:
              readers: [Watermark]
              staging: []
              writers: []
            """;

        var descriptor = DriverDescriptorReader.Deserialize(yaml);
        var libraries = new LibraryRegistry(Path.GetTempPath());
        var driver = (IConnectionPreviewer)DriverDescriptorReader.BuildDriver(descriptor, libraries, repoRoot);

        var config = new ConnectionConfig
        {
            Name = "jdbc-descriptor-partial-keys-test", DriverType = "postgres-via-jdbc-partial-keys",
            Host = "localhost", Port = 15432, Database = db.DatabaseName,
            AuthMode = AuthMode.SqlAuth, UserId = "dbdatasync",
        };

        var preview = driver.PreviewConnection(config);

        // The template has no {host}/{port}/{database} tokens, so all three fall back to properties —
        // under JDBC's own default key spellings, not the ADO.NET ones a naive reuse of
        // DescriptorConnectionStringKeysYaml would have silently applied to everything except username.
        Assert.Equal("localhost", preview.Properties["host"]);
        Assert.Equal("15432", preview.Properties["port"]);
        Assert.Equal(db.DatabaseName, preview.Properties["database"]);
        Assert.Equal("dbdatasync", preview.Properties["pguser"]);
        Assert.False(preview.Properties.ContainsKey("Host"));
    }

    /// <summary>
    /// Real bug report: "using a JDBC connector and a bulk load with a segmenting strategy is giving an
    /// error saying that the minimum segment parameter is missing from the provided parameters." Root
    /// cause, found via a live repro rather than guessed: <c>JdbcCommand</c>'s name -> ordinal-<c>?</c>
    /// translation always looks up a <em>bare</em> parameter name, but <see cref="DescriptorDialect"/>
    /// only renders one if the descriptor's own <c>parameterNameIsBare</c> is <c>true</c> — a flag every
    /// test above sets explicitly, which is exactly why none of them caught this. An unsegmented read
    /// binds no parameters and never reaches that code, so the gap is invisible until the first
    /// segmenting bulk load binds a range parameter (<c>segMin</c>/<c>segMax</c>) and
    /// <c>JdbcCommand.TranslateParameters</c> looks for the bare name against a <c>DbParameter</c> whose
    /// <c>ParameterName</c> still carries the <c>@</c> sigil.
    /// <para>
    /// Fixed by forcing <c>ParameterNameIsBare</c> true in <see cref="JdbcGenericDriver.FromDescriptor"/>
    /// regardless of what the yaml says — this descriptor sets it to <c>false</c> explicitly (not just
    /// omits it) to prove the override, not just a better default, is what's under test.
    /// </para>
    /// </summary>
    [Fact]
    public async Task ASegmentingRead_BindsItsRangeParameters_EvenWhenTheYamlGetsParameterNameIsBareWrong()
    {
        var repoRoot = Path.Combine(Path.GetTempPath(), $"jdbc-descriptor-segment-test-{Guid.NewGuid():N}");
        var filesDir = Path.Combine(repoRoot, "files");
        Directory.CreateDirectory(filesDir);
        File.Copy(Path.Combine(AppContext.BaseDirectory, "postgresql.jar"), Path.Combine(filesDir, "postgresql.jar"));

        const string yaml = """
            id: postgres-via-jdbc-segmented
            displayName: Postgres (via JDBC, segmented read)
            library: ikvm
            base: DbDataSync.Drivers.Jdbc.JdbcGenericDriver, DbDataSync.Drivers.Jdbc
            jdbc:
              driverClass: org.postgresql.Driver
              driverJarPaths: [postgresql.jar]
            dialect:
              quoteIdentifier: doubleQuote
              parameterPrefix: "@"
              parameterNameIsBare: false
              rowLimit: limitOffset
            typeMap:
              int4: Int32
              "varchar(n)": { kind: String, length: n, unicode: true }
            capabilities:
              readers: [Watermark, BatchReload]
              staging: []
              writers: []
            """;

        var descriptor = DriverDescriptorReader.Deserialize(yaml);
        var libraries = new LibraryRegistry(Path.GetTempPath());
        var driver = DriverDescriptorReader.BuildDriver(descriptor, libraries, repoRoot);

        var config = new ConnectionConfig
        {
            Name = "jdbc-descriptor-segment-test",
            DriverType = "postgres-via-jdbc-segmented",
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
        var sourceColumns = new List<CachedColumn>
        {
            new("id", "int4", false, true, false),
            new("name", "varchar", false, false, false),
        };
        // [1, 3) — both fixture rows (id 1 and 2) fall inside; RangeSegment is what an expanded
        // AutoSegment bucket becomes, and is exactly what binds a segMin/segMax parameter pair.
        var segment = new RangeSegment("id", "1", "3");
        var options = new Dictionary<string, string> { [SegmentSerializer.SegmentOptionKey] = SegmentSerializer.Serialize(segment) };

        var result = await reader.ReadChangesAsync(
            connection, source, null, ReadIntent.InitialLoad, mappings, "descriptor-segment-test", sourceColumns,
            options, CancellationToken.None);

        var rows = new List<ChangeRow>();
        await foreach (var row in result.Rows)
            rows.Add(row);

        Assert.Equal(2, rows.Count);
        Assert.Contains(rows, r => (int)r["id"]! == 1 && (string)r["name"]! == "alice");
        Assert.Contains(rows, r => (int)r["id"]! == 2 && (string)r["name"]! == "bob");
    }

    /// <summary>
    /// A driver.yaml's own testQuery, round-tripped through real YAML text — not a directly-constructed
    /// JdbcDriverSpec, which is what JdbcConnectionTests' own DefaultTestQuery test uses and which
    /// skips YAML parsing entirely. Deliberately uses a `::` cast, the everyday punctuation a real
    /// Postgres-flavoured test query would carry, to catch a YAML scalar being cut short or mis-parsed
    /// by something a plain `SELECT 1` would never expose.
    /// </summary>
    [Fact]
    public async Task ADriverYamlsOwnTestQuery_ParsesInFull_AndRunsOverARealJdbcConnection()
    {
        var repoRoot = Path.Combine(Path.GetTempPath(), $"jdbc-testquery-descriptor-test-{Guid.NewGuid():N}");
        var filesDir = Path.Combine(repoRoot, "files");
        Directory.CreateDirectory(filesDir);
        File.Copy(Path.Combine(AppContext.BaseDirectory, "postgresql.jar"), Path.Combine(filesDir, "postgresql.jar"));

        const string yaml = """
            id: postgres-via-jdbc-testquery
            displayName: Postgres (via JDBC)
            library: ikvm
            base: DbDataSync.Drivers.Jdbc.JdbcGenericDriver, DbDataSync.Drivers.Jdbc
            jdbc:
              driverClass: org.postgresql.Driver
              driverJarPaths: [postgresql.jar]
            testQuery: SELECT 1::int AS one, current_database() AS db

            dialect:
              quoteIdentifier: doubleQuote
              parameterPrefix: "@"
              parameterNameIsBare: true
              rowLimit: limitOffset
            typeMap:
              int4: Int32
            capabilities:
              readers: []
              staging: []
              writers: []
            """;

        var descriptor = DriverDescriptorReader.Deserialize(yaml);
        Assert.Equal("SELECT 1::int AS one, current_database() AS db", descriptor.TestQuery);

        var driver = DriverDescriptorReader.BuildDriver(descriptor, new LibraryRegistry(Path.GetTempPath()), repoRoot);
        var tester = Assert.IsAssignableFrom<IConnectionTester>(driver);
        Assert.Equal("SELECT 1::int AS one, current_database() AS db", tester.DefaultTestQuery);

        var config = new ConnectionConfig
        {
            Name = "jdbc-testquery-descriptor-test",
            DriverType = "postgres-via-jdbc-testquery",
            ConnectionString = $"{JdbcTestDatabase.JdbcUrl}{db.DatabaseName}",
            AuthMode = AuthMode.SqlAuth,
            UserId = "dbdatasync",
        };
        await using var connection = driver.CreateConnection(config, "DbDataSync_Test_Pw1");
        connection.Open();

        using var cmd = connection.CreateCommand();
        cmd.CommandText = tester.DefaultTestQuery;
        await using var reader = await cmd.ExecuteReaderAsync();

        Assert.True(await reader.ReadAsync());
        Assert.Equal(2, reader.FieldCount);
        Assert.Equal("one", reader.GetName(0));
        Assert.Equal(1, reader.GetInt32(0));
    }
}
