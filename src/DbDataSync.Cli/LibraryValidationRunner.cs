using System.Data.Common;
using ClrKernel.Core.Secrets;
using DbDataSync.Core.Config;
using DbDataSync.Core.Sql;
using DbDataSync.Drivers.Abstractions;
using DbDataSync.Drivers.Generic;

namespace DbDataSync.Cli;

/// <summary>The result of a phase 109j deep validation run — what <c>LibraryCommand.ValidateAsync</c>
/// reports to the operator (or, spawned from the API, what the parent process reads back from this
/// child's exit code and stdout).</summary>
public sealed record LibraryValidationResult(bool Succeeded, string? Error, string? StagingProviderKind, string? WriterKind, long RowsWritten)
{
    public static LibraryValidationResult Ok(string stagingProviderKind, string writerKind, long rowsWritten) =>
        new(true, null, stagingProviderKind, writerKind, rowsWritten);

    public static LibraryValidationResult Failed(string error) => new(false, error, null, null, 0);
}

/// <summary>
/// Phase 109j item 4: the deep, connection-scoped compatibility check. Drives <paramref name="driver"/>'s
/// own *native* (first-registered — see <see cref="IDriver.StagingProviders"/>/<see cref="IDriver.Writers"/>'s
/// own ordering, e.g. <c>MsSqlStagingTableProvider</c>/<c>MsSqlMergeWriter</c> ahead of the portable
/// generic fallbacks) staging provider and writer against a real scratch table it creates and drops on
/// the connection's own target, with a handful of hand-built synthetic rows — the exact real
/// <c>SqlBulkCopy</c>/typed-parameter machinery a real replication pass uses, proven against whichever
/// library version is actually installed, not merely constructed.
/// <para>
/// **Deliberately reuses the driver's own real <see cref="IStagingProvider"/>/<see cref="IChangeWriter"/>
/// rather than a new per-driver probe interface** — the phase doc's own explicit reversal of an earlier
/// draft. Whatever a real pass would do is exactly what this does, so there is nothing here that could
/// drift from what the driver actually exercises in production.
/// </para>
/// </summary>
public static class LibraryValidationRunner
{
    /// <summary>The known-name prefix an orphaned scratch table (a crashed run, not a caught exception)
    /// is still identifiable and safe to drop by — see <see cref="SweepStaleTablesAsync"/>.</summary>
    public const string TablePrefix = "DbDataSync_LibraryValidation_";

    private static readonly string[] SyntheticNames = ["phase-109j-alpha", "phase-109j-beta", "phase-109j-gamma"];

    public static async Task<LibraryValidationResult> RunAsync(
        IDriver driver, ConnectionConfig connection, SecretStore secretStore, CancellationToken cancellationToken)
    {
        DbConnection dbConnection;
        try
        {
            var credential = connection.AuthMode == AuthMode.SqlAuth
                ? secretStore.Resolve(connection.CredentialSecretRef!)
                : null;

            // Constructing the connection (not just opening it) is where a genuinely broken installed
            // library surfaces, per this whole phase's own reasoning: the CLR's per-method JIT touches
            // the library assembly the moment CreateConnection's own field initializers/constructor
            // calls run, which can throw FileNotFoundException/FileLoadException/BadImageFormatException
            // /TypeLoadException — exactly the class of failure this deep check exists to surface as a
            // clean, diagnosable result rather than an unhandled crash of this (already-isolated) child
            // process. SecretNotFoundException is caught here too: an unresolvable credential is a
            // config problem this check can name, not a reason to crash.
            dbConnection = driver.CreateConnection(connection, credential);
        }
        catch (Exception ex) when (ex is DbException or InvalidOperationException or FileNotFoundException
            or FileLoadException or BadImageFormatException or TypeLoadException or MissingMethodException
            or ClrKernel.Core.Secrets.SecretNotFoundException)
        {
            return LibraryValidationResult.Failed($"Could not build a connection for '{connection.Name}': {ex.Message}");
        }

        await using (dbConnection)
        {
            try
            {
                await dbConnection.OpenAsync(cancellationToken);
            }
            catch (Exception ex) when (ex is DbException or InvalidOperationException or FileNotFoundException
                or FileLoadException or BadImageFormatException or TypeLoadException or MissingMethodException)
            {
                return LibraryValidationResult.Failed($"Could not open connection '{connection.Name}': {ex.Message}");
            }

            // The connection's own idea of "current database" (SqlConnection/NpgsqlConnection both
            // surface this via the base DbConnection.Database property), not connection.Database — a
            // connection-string-only connection may carry the database inside the string rather than as
            // a separate config field, and this is the one value both engines agree reflects reality
            // once open.
            var database = dbConnection.Database;
            var schema = DefaultSchema(driver.DriverType);
            var tableName = $"{TablePrefix}{Guid.NewGuid():N}";
            var target = new TableRef { ConnectionName = connection.Name, Database = database, Schema = schema, Table = tableName };

            // Open question 3 (phase doc): decided yes — a best-effort sweep of any stale scratch table
            // left by a previous crashed run (not a caught exception, which always reaches this
            // method's own `finally` below) before creating a new one. Failure to sweep is not fatal to
            // this run; it just means one more identifiable, safe-to-drop-by-hand orphan sits until the
            // next validate call.
            await SweepStaleTablesAsync(driver, dbConnection, database, schema, cancellationToken);

            try
            {
                using var createCmd = dbConnection.CreateTimedCommand();
                createCmd.CommandText = BuildCreateScratchTableSql(driver, schema, tableName);
                await createCmd.ExecuteNonQueryAsync(cancellationToken);
            }
            catch (DbException ex)
            {
                return LibraryValidationResult.Failed(
                    $"Could not create the scratch table '{schema}.{tableName}' on '{database}' — the " +
                    $"connection's credential most likely lacks CREATE TABLE rights there. Underlying error: {ex.Message}");
            }

            var stagingProvider = driver.StagingProviders[0];
            var writer = driver.Writers[0];
            StagedChangeSet? staged = null;
            try
            {
                var columnMappings = BuildColumnMappings();
                var targetColumns = BuildTargetColumns(driver.DriverType);
                var rows = BuildSyntheticRows();

                staged = await stagingProvider.StageAsync(
                    dbConnection, target, ToAsyncEnumerable(rows), columnMappings, "phase-109j-library-validation",
                    targetColumns, new Dictionary<string, string>(), cancellationToken);

                var writeResult = await writer.ApplyAsync(
                    dbConnection, target, staged, columnMappings, "phase-109j-library-validation", targetColumns,
                    new Dictionary<string, string>(), cancellationToken);

                var landed = await CountRowsAsync(driver, dbConnection, schema, tableName, cancellationToken);
                if (landed != rows.Count)
                {
                    return LibraryValidationResult.Failed(
                        $"Staged and applied via {stagingProvider.Kind}/{writer.Kind}, but {landed} of " +
                        $"{rows.Count} synthetic row(s) actually landed in '{schema}.{tableName}'.");
                }

                return LibraryValidationResult.Ok(stagingProvider.Kind, writer.Kind, writeResult.RowsWritten);
            }
            catch (Exception ex) when (ex is DbException or InvalidOperationException or FileNotFoundException
                or FileLoadException or BadImageFormatException or TypeLoadException or MissingMethodException)
            {
                // MissingMethodException/MissingFieldException-shaped failures here are the exact
                // scenario this whole phase exists to surface — a member the static IL-surface check
                // either doesn't know to look for (a property/method signature quirk it wasn't precise
                // enough to catch) or one the installed library changed since that check last ran.
                // Reported as a real, specific failure naming the two Kinds involved, not a generic crash.
                return LibraryValidationResult.Failed(
                    $"{stagingProvider.Kind}/{writer.Kind} failed against the real connection: {ex.Message}");
            }
            finally
            {
                // Deterministic teardown even on failure — the phase doc's own explicit requirement.
                // Both steps are best-effort: a table this run never got as far as creating, or a
                // staging location the provider itself already dropped, must not turn cleanup into a
                // second failure that hides the real one above.
                if (staged is not null)
                {
                    try
                    {
                        await stagingProvider.CleanupAsync(dbConnection, staged, cancellationToken);
                    }
                    catch (DbException)
                    {
                        // Best-effort — the staging location may already be gone.
                    }
                }

                try
                {
                    using var dropCmd = dbConnection.CreateTimedCommand();
                    dropCmd.CommandText = $"DROP TABLE IF EXISTS {QuoteTable(driver, schema, tableName)};";
                    await dropCmd.ExecuteNonQueryAsync(cancellationToken);
                }
                catch (DbException)
                {
                    // Best-effort — an orphan here is still identifiable by TablePrefix and safe to
                    // drop by hand, or will be swept by the next validate run against this same
                    // connection.
                }
            }
        }
    }

    /// <summary>Drops any previous run's scratch table this connection can still see, by listing tables
    /// via the driver's own <see cref="IDriver.ListTablesAsync"/> (already-shipped metadata browsing,
    /// not new SQL) and filtering to <see cref="TablePrefix"/>. Best-effort: a failure here (a
    /// permission the credential doesn't have, an engine quirk) is swallowed rather than failing the
    /// whole validate run over housekeeping for a *previous* run.</summary>
    private static async Task SweepStaleTablesAsync(
        IDriver driver, DbConnection connection, string database, string schema, CancellationToken cancellationToken)
    {
        try
        {
            var tables = await driver.ListTablesAsync(connection, database, cancellationToken);
            foreach (var stale in tables.Where(t => t.Table.StartsWith(TablePrefix, StringComparison.Ordinal)))
            {
                try
                {
                    using var dropCmd = connection.CreateTimedCommand();
                    dropCmd.CommandText = $"DROP TABLE IF EXISTS {QuoteTable(driver, stale.Schema, stale.Table)};";
                    await dropCmd.ExecuteNonQueryAsync(cancellationToken);
                }
                catch (DbException)
                {
                    // Best-effort per stale table — one that can't be dropped (e.g. someone else's
                    // in-flight run) shouldn't stop this run or the sweep of any others.
                }
            }
        }
        catch (Exception ex) when (ex is DbException or InvalidOperationException)
        {
            // Listing tables itself failed — proceed to create this run's own table anyway; a stale
            // orphan (if any) is still identifiable by TablePrefix and safe to drop by hand.
            _ = ex;
        }
    }

    private static string DefaultSchema(string driverType) => driverType switch
    {
        DriverIds.MsSql => "dbo",
        _ => "public",
    };

    /// <summary>The scratch schema's three per-engine type spellings, shared by
    /// <see cref="BuildCreateScratchTableSql"/> (the real DDL) and <see cref="BuildTargetColumns"/> (the
    /// metadata describing that same DDL to the writer) — one source, so the two can never drift apart
    /// the way an earlier version of this file did (see <see cref="BuildTargetColumns"/>'s own doc
    /// comment for the real bug that found it).</summary>
    private static (string IntType, string StringType, string TimestampType) ScratchColumnTypes(string driverType) =>
        driverType switch
        {
            DriverIds.MsSql => ("INT", "NVARCHAR(200)", "DATETIME2"),
            _ => ("INTEGER", "VARCHAR(200)", "TIMESTAMP"),
        };

    /// <summary>The scratch schema, per the phase doc: an int key, a string, a timestamp. Column type
    /// spelling is per-engine (MsSql vs. ANSI-ish), the same three logical columns either way. Every
    /// identifier is quoted through the driver's own <see cref="SqlDialect.QuoteIdentifier"/> — see
    /// <see cref="QuoteTable"/>'s own doc comment for why an unquoted name is a real bug on Postgres.
    /// </summary>
    private static string BuildCreateScratchTableSql(IDriver driver, string schema, string table)
    {
        var (intType, stringType, timestampType) = ScratchColumnTypes(driver.DriverType);
        var dialect = Dialect(driver);
        string Id(string name) => dialect?.QuoteIdentifier(name) ?? name;

        return $"CREATE TABLE {QuoteTable(driver, schema, table)} (" +
               $"{Id("Id")} {intType} NOT NULL PRIMARY KEY, " +
               $"{Id("Name")} {stringType} NULL, " +
               $"{Id("UpdatedAt")} {timestampType} NULL);";
    }

    /// <summary>
    /// <c>schema.table</c>, each half quoted through the driver's own dialect when it exposes one
    /// (<see cref="IDialectProvider"/> — both <c>MsSqlDriver</c> and <c>PostgresDriver</c> do; a
    /// hypothetical future driver with neither a native staging provider/writer nor a dialect never
    /// reaches this method at all, since <c>LibraryCommand.ValidateAsync</c> already refuses it before
    /// calling in). **Real bug found running this against the real Postgres container**: an unquoted
    /// mixed-case identifier (<c>DbDataSync_LibraryValidation_&lt;guid&gt;</c>) is silently folded to
    /// all-lowercase by Postgres's own unquoted-identifier rule at <c>CREATE TABLE</c> time, while
    /// <c>BatchInsertStagingProvider</c>/<c>DeleteInsertWriter</c> (Postgres's own first-registered,
    /// "native" pipeline — it has no bespoke staging provider, see 109h's own table) quote the *exact*
    /// case they were given when referencing the table back — a mismatch that failed with <c>relation
    /// "public.DbDataSync_LibraryValidation_..." does not exist</c> the first time this ran for real.
    /// Quoting this method's own DDL/DML the identical way makes the two agree.
    /// </summary>
    private static string QuoteTable(IDriver driver, string schema, string table)
    {
        var dialect = Dialect(driver);
        return dialect is null ? $"{schema}.{table}" : $"{dialect.QuoteIdentifier(schema)}.{dialect.QuoteIdentifier(table)}";
    }

    private static SqlDialect? Dialect(IDriver driver) => (driver as IDialectProvider)?.Dialect;

    private static List<ColumnMapping> BuildColumnMappings() =>
    [
        new() { SourceColumn = "Id", TargetColumn = "Id" },
        new() { SourceColumn = "Name", TargetColumn = "Name" },
        new() { SourceColumn = "UpdatedAt", TargetColumn = "UpdatedAt" },
    ];

    /// <summary>
    /// **Must be real, engine-native type spellings, not placeholder words** — a real bug found running
    /// this against the real Postgres container: <see cref="DbDataSync.Drivers.Generic.BatchInsertStagingProvider"/>
    /// (Postgres's own first-registered, "native" staging provider — it has no bespoke one, see 109h's
    /// own table) reads <see cref="CachedColumn.NativeType"/> directly into its staging table's DDL,
    /// unlike <c>MsSqlStagingTableProvider</c>, which re-discovers the *real* target table's column
    /// types live and ignores this value entirely. An earlier version of this method used cosmetic
    /// English words ("int"/"string"/"datetime") here, which worked against MsSql purely because MsSql
    /// never looks at them, and failed loudly against Postgres with <c>42704: type "string" does not
    /// exist</c> the first time this was actually run against a real Postgres container. Matches
    /// <see cref="BuildCreateScratchTableSql"/>'s own per-engine spellings exactly, for the same reason
    /// they must agree: this is metadata *about* that table, not a separate guess at it.
    /// </summary>
    private static List<CachedColumn> BuildTargetColumns(string driverType)
    {
        var (intType, stringType, timestampType) = ScratchColumnTypes(driverType);

        return
        [
            new("Id", intType, isNullable: false, isPrimaryKey: true, isIdentity: false),
            new("Name", stringType, isNullable: true, isPrimaryKey: false, isIdentity: false),
            new("UpdatedAt", timestampType, isNullable: true, isPrimaryKey: false, isIdentity: false),
        ];
    }

    private static List<ChangeRow> BuildSyntheticRows()
    {
        var schema = new ChangeSchema(["Id", "Name", "UpdatedAt"]);
        var now = DateTime.UtcNow;
        var rows = new List<ChangeRow>();
        for (var i = 0; i < SyntheticNames.Length; i++)
            rows.Add(new ChangeRow(ChangeOperation.Insert, schema, [i + 1, SyntheticNames[i], now]));
        return rows;
    }

    private static async Task<long> CountRowsAsync(
        IDriver driver, DbConnection connection, string schema, string table, CancellationToken cancellationToken)
    {
        using var cmd = connection.CreateTimedCommand();
        cmd.CommandText = $"SELECT COUNT(*) FROM {QuoteTable(driver, schema, table)};";
        var value = await cmd.ExecuteScalarAsync(cancellationToken);
        return Convert.ToInt64(value);
    }

#pragma warning disable CS1998 // yields synchronously; IAsyncEnumerable is what IStagingProvider needs.
    private static async IAsyncEnumerable<ChangeRow> ToAsyncEnumerable(IReadOnlyList<ChangeRow> rows)
    {
        foreach (var row in rows)
            yield return row;
    }
#pragma warning restore CS1998
}
