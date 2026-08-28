using System.Data.Common;
using System.Globalization;
using DataSync.Core.Config;
using DataSync.Drivers.Abstractions;
using DataSync.Drivers.Generic;
using DataSync.Scripting;
using DataSync.Scripting.Abstractions;

namespace DataSync.Api.Services;

/// <summary>What a script did to one input.</summary>
/// <param name="Note">Why the output is what it is, when that is not obvious — "dropped", "unchanged".</param>
public sealed record ScriptTestCase(string Input, string? Output, string? Note = null);

/// <param name="Source">
/// Where the input came from, in words the operator sees. This is a safety property, not a caption:
/// "generated sample" and "live query against 'prod-src'" must never be confusable, because one of
/// them touched a real system.
/// </param>
/// <param name="Statement">The SQL a live test ran, or the statement a query builder produced. Shown
/// because a query against production that the operator cannot see is exactly what this is avoiding.</param>
public sealed record ScriptTestResult(
    string Mode,
    string Source,
    IReadOnlyList<ScriptTestCase> Cases,
    IReadOnlyList<string> Log,
    string? Statement = null,
    string? Error = null);

/// <summary>What to run the script against.</summary>
/// <param name="ConnectionName">Set only for a live test. Empty by default, and choosing one is a
/// deliberate act — there is no fallback from generated to live, because a silent one would mean an
/// operator querying a real system without having said so.</param>
public sealed record ScriptTestRequest(
    ScriptDefinition Script,
    string? ReplicationName = null,
    string? MappingName = null,
    string? ConnectionName = null,
    int SampleRows = 5);

/// <summary>
/// Runs a script against sample input and reports what it did.
/// <para>
/// Compiling proves a script is C#. It proves nothing about whether it produces the SQL, the value or
/// the rows the operator meant — and for a change query in particular, being wrong is not a crash but
/// a target that silently disagrees with its source. This is the button between writing one and
/// finding out on a real pass.
/// </para>
/// </summary>
public sealed class ScriptTestService(
    ConfigRepository configRepository,
    ScriptCompiler compiler,
    DriverConnectionFactory connections,
    MetadataService metadataService)
{
    private const int MaxLiveRows = 20;

    public async Task<ScriptTestResult> RunAsync(ScriptTestRequest request, CancellationToken cancellationToken)
    {
        var manifest = request.Script.Manifest;
        var log = new List<string>();

        var live = !string.IsNullOrWhiteSpace(request.ConnectionName);

        try
        {
            var compiled = Compile(request.Script);
            var columns = await ResolveColumnsAsync(request, live, cancellationToken);
            var mappings = ResolveMappings(request, columns);
            var dialect = await ResolveDialectAsync(request, cancellationToken);
            var source = DescribeSource(request, live, dialect);

            var cases = manifest.Kind switch
            {
                ScriptSlots.SqlColumnExpression => TestSqlColumnExpression(
                    compiled.CreateInstance<ISqlColumnExpression>(manifest.Name), mappings, columns, dialect, manifest),
                ScriptSlots.ValueColumnExpression => await TestValueColumnExpressionAsync(
                    compiled.CreateInstance<IValueColumnExpression>(manifest.Name),
                    mappings, columns, dialect, manifest, request, live, cancellationToken),
                ScriptSlots.RowTransform => await TestRowTransformAsync(
                    compiled.CreateInstance<IRowTransform>(manifest.Name),
                    mappings, columns, dialect, manifest, log, request, live, cancellationToken),
                _ => throw new ConfigValidationException(
                    $"Testing a '{manifest.Kind}' script is not supported yet — see phase 41."),
            };

            return new ScriptTestResult(live ? "live" : "generated", source, cases, log);
        }
        // Deliberately broad, and deliberately not a 500. A script that throws is exactly what this
        // button is for — an operator finds out here that theirs throws on a null, rather than in the
        // middle of a pass — so whatever it threw is the result, not a fault of the server's.
        //
        // The two exceptions rethrown are the ones that are the *caller's* mistake rather than the
        // script's: a mapping that does not exist, or a live test with nothing to read.
        catch (Exception ex) when (ex is not (ConfigValidationException or FileNotFoundException))
        {
            return new ScriptTestResult(
                live ? "live" : "generated", DescribeSource(request, live, AnsiScriptDialect.Instance), [], log,
                Error: ex is ScriptCompilationFailure ? ex.Message : $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    private ScriptCompilation Compile(ScriptDefinition script)
    {
        var compiled = compiler.Compile(script);
        if (!compiled.Success)
        {
            throw new ScriptCompilationFailure(
                $"Script '{script.Manifest.Name}' did not compile: " +
                string.Join("; ", compiled.Diagnostics.Select(d => $"({d.Line},{d.Column}) {d.Message}")));
        }
        return compiled;
    }

    // ---- Per-slot tests ----

    /// <summary>
    /// The cheapest slot to test, because nothing has to run: the script returns a string, and the
    /// string is the answer. Null means "leave the column alone", which is a real result and is shown
    /// as one rather than as an empty cell.
    /// </summary>
    private static IReadOnlyList<ScriptTestCase> TestSqlColumnExpression(
        ISqlColumnExpression script, IReadOnlyList<ColumnMapping> mappings,
        IReadOnlyList<ColumnMetadata> columns, IScriptDialect dialect, ScriptConfig manifest)
    {
        var parameters = new ScriptParameters(DefaultParameters(manifest));
        var byName = columns.ToDictionary(c => c.Name, StringComparer.OrdinalIgnoreCase);

        return mappings.Select(mapping =>
        {
            byName.TryGetValue(mapping.SourceColumn, out var column);
            var reference = dialect.QuoteIdentifier(mapping.SourceColumn);
            var sql = script.RenderSql(new SqlColumnExpressionContext(
                mapping.SourceColumn, mapping.TargetColumn, column, reference, dialect, parameters));

            return new ScriptTestCase(
                $"{mapping.SourceColumn} → {mapping.TargetColumn}" + (column is null ? "" : $" ({column.NativeType})"),
                sql,
                sql is null ? "left as it would have been" : null);
        }).ToList();
    }

    private async Task<IReadOnlyList<ScriptTestCase>> TestValueColumnExpressionAsync(
        IValueColumnExpression script, IReadOnlyList<ColumnMapping> mappings,
        IReadOnlyList<ColumnMetadata> columns, IScriptDialect dialect, ScriptConfig manifest,
        ScriptTestRequest request, bool live, CancellationToken cancellationToken)
    {
        var parameters = new ScriptParameters(DefaultParameters(manifest));
        var declared = script.DeclareColumns(new ValueColumnDeclarationContext(mappings, dialect, parameters));

        if (declared.Count == 0)
            return [new ScriptTestCase("(no columns declared)", null, "This script transforms nothing as configured.")];

        var samples = live
            ? await LiveValuesAsync(request, declared, cancellationToken)
            : GeneratedValues(columns, declared, request.SampleRows);

        return samples.Select(sample =>
        {
            var mapping = mappings.FirstOrDefault(m =>
                string.Equals(m.SourceColumn, sample.Column, StringComparison.OrdinalIgnoreCase));
            var output = script.Evaluate(sample.Value, new ValueColumnExpressionContext(
                sample.Column, mapping?.TargetColumn ?? sample.Column, dialect, parameters));

            return new ScriptTestCase(
                $"{sample.Column} = {Format(sample.Value)}",
                Format(output),
                Equals(output, sample.Value) ? "unchanged" : null);
        }).ToList();
    }

    /// <summary>
    /// The one slot whose generated input has to include a **delete**. A delete carries only its key —
    /// every other column is null — and it is the case a row transform is most likely to mishandle,
    /// because it is the one an operator never types into their head while writing the happy path.
    /// </summary>
    private async Task<IReadOnlyList<ScriptTestCase>> TestRowTransformAsync(
        IRowTransform script, IReadOnlyList<ColumnMapping> mappings,
        IReadOnlyList<ColumnMetadata> columns, IScriptDialect dialect, ScriptConfig manifest,
        List<string> log, ScriptTestRequest request, bool live, CancellationToken cancellationToken)
    {
        var parameters = new ScriptParameters(DefaultParameters(manifest));
        var context = new RowTransformContext(mappings, dialect, parameters, log.Add);

        var rows = live
            ? await LiveRowsAsync(request, columns, cancellationToken)
            : GeneratedRows(columns, mappings);

        var declaredSchema = script.DeclareSchema(rows.Count > 0 ? rows[0].Schema : new ChangeSchema([]), context);

        var cases = new List<ScriptTestCase>();
        foreach (var row in rows)
        {
            var output = await script.TransformAsync(row, context, cancellationToken);
            cases.Add(new ScriptTestCase(
                $"{row.Operation}: {FormatRow(row)}",
                output is null ? null : FormatRow(output),
                output is null ? "dropped" : null));
        }

        if (!declaredSchema.ColumnNames.SequenceEqual(rows.FirstOrDefault()?.Schema.ColumnNames ?? [], StringComparer.OrdinalIgnoreCase))
        {
            cases.Insert(0, new ScriptTestCase(
                "Declared schema", string.Join(", ", declaredSchema.ColumnNames),
                "This transform changes the row's shape, so staging builds its table from this rather " +
                "than from what the reader produced."));
        }

        return cases;
    }

    // ---- Sample input ----

    private sealed record ValueSample(string Column, object? Value);

    private static IReadOnlyList<ValueSample> GeneratedValues(
        IReadOnlyList<ColumnMetadata> columns, IReadOnlyList<string> declared, int limit)
    {
        var samples = new List<ValueSample>();
        foreach (var name in declared)
        {
            var column = columns.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));
            var type = column is null
                ? new CanonicalType(CanonicalTypeKind.String, 50, null, null, true, false)
                : CanonicalTypeOf(column);

            foreach (var value in SampleValues.For(type, column?.IsNullable ?? true).Take(Math.Max(1, limit)))
                samples.Add(new ValueSample(name, value));
        }
        return samples;
    }

    /// <summary>
    /// One row per operation. The insert and update are fully populated; the delete carries its key
    /// and nothing else, which is what a real one looks like.
    /// </summary>
    private static IReadOnlyList<ChangeRow> GeneratedRows(
        IReadOnlyList<ColumnMetadata> columns, IReadOnlyList<ColumnMapping> mappings)
    {
        var names = columns.Count > 0
            ? columns.Select(c => c.Name).ToList()
            : mappings.Select(m => m.SourceColumn).Distinct().ToList();
        if (names.Count == 0)
            return [];

        var schema = new ChangeSchema(names);

        object?[] Populated() => [.. names.Select(name =>
        {
            var column = columns.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));
            var type = column is null
                ? new CanonicalType(CanonicalTypeKind.String, 50, null, null, true, false)
                : CanonicalTypeOf(column);
            return SampleValues.For(type, isNullable: false).FirstOrDefault();
        })];

        var keyNames = columns.Where(c => c.IsPrimaryKey).Select(c => c.Name).ToList();
        if (keyNames.Count == 0)
            keyNames = [names[0]];

        var deleteValues = Populated();
        for (var i = 0; i < names.Count; i++)
        {
            if (!keyNames.Contains(names[i], StringComparer.OrdinalIgnoreCase))
                deleteValues[i] = null;
        }

        return
        [
            new ChangeRow(ChangeOperation.Insert, schema, Populated()),
            new ChangeRow(ChangeOperation.Update, schema, Populated()),
            new ChangeRow(ChangeOperation.Delete, schema, deleteValues),
        ];
    }

    private async Task<IReadOnlyList<ValueSample>> LiveValuesAsync(
        ScriptTestRequest request, IReadOnlyList<string> declared, CancellationToken cancellationToken)
    {
        var rows = await LiveRowsAsync(request, [], cancellationToken);
        return
        [
            .. rows.SelectMany(row => declared
                .Where(name => row.Schema.TryGetOrdinal(name, out _))
                .Select(name => new ValueSample(name, row[name]))),
        ];
    }

    /// <summary>
    /// Real rows, from a real table, on a connection the operator picked by hand. Capped, and the cap
    /// is visible in the result — a limit an operator can see beats one they cannot.
    /// </summary>
    private async Task<IReadOnlyList<ChangeRow>> LiveRowsAsync(
        ScriptTestRequest request, IReadOnlyList<ColumnMetadata> columns, CancellationToken cancellationToken)
    {
        var (source, _) = ResolveTables(request);
        if (source is null)
            throw new ConfigValidationException("A live test needs a table mapping to know which table to read.");

        var (connection, driver) = await connections.OpenAsync(request.ConnectionName!, cancellationToken);
        try
        {
            var dialect = driver is IDialectProvider provider
                ? provider.Dialect
                : throw new ConfigValidationException($"The '{driver.DriverType}' driver does not name a SQL dialect.");

            await dialect.UseDatabaseAsync(connection, source.Database, cancellationToken);

            using var cmd = connection.CreateCommand();
            cmd.CommandText = dialect.RenderSampleSelect(
                dialect.QualifyTable(source.Schema, source.Table), Math.Clamp(request.SampleRows, 1, MaxLiveRows));

            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
            var schema = ResultSetSchema.From(reader);
            var rows = new List<ChangeRow>();
            while (await reader.ReadAsync(cancellationToken))
                rows.Add(new ChangeRow(ChangeOperation.Insert, schema, ResultSetSchema.ReadValues(reader, schema.Count)));
            return rows;
        }
        finally
        {
            await connection.DisposeAsync();
        }
    }

    // ---- Context resolution ----

    private (SourceTableRef? Source, TableRef? Target) ResolveTables(ScriptTestRequest request)
    {
        if (request.ReplicationName is null || request.MappingName is null)
            return (null, null);

        var task = configRepository.LoadReplicationTask(request.ReplicationName);
        var mapping = configRepository.LoadTableMapping(request.ReplicationName, request.MappingName);
        if (mapping.Sources.Count == 0 || mapping.Targets.Count == 0)
            return (null, null);

        return (EndpointResolution.ResolveSource(task, mapping.Sources[0]),
                EndpointResolution.ResolveTarget(task, mapping.Targets[0]));
    }

    /// <summary>
    /// The mapping's own columns where there is a mapping, and a plausible pair where there is not —
    /// so a script can be tested the moment it is written, before it is bound to anything.
    /// </summary>
    private async Task<IReadOnlyList<ColumnMetadata>> ResolveColumnsAsync(
        ScriptTestRequest request, bool live, CancellationToken cancellationToken)
    {
        var (source, _) = ResolveTables(request);
        if (source is null)
            return DefaultColumns;

        try
        {
            return await metadataService.ListColumnsAsync(
                source.ConnectionName, source.Database, source.Schema, source.Table, cancellationToken);
        }
        catch (Exception ex) when (!live && ex is DbException or InvalidOperationException)
        {
            // A generated test must not need a reachable database. Falling back keeps the button
            // working when the source is down, which is when someone is most likely to be editing.
            return DefaultColumns;
        }
    }

    private IReadOnlyList<ColumnMapping> ResolveMappings(ScriptTestRequest request, IReadOnlyList<ColumnMetadata> columns)
    {
        if (request.ReplicationName is not null && request.MappingName is not null)
        {
            var mapping = configRepository.LoadTableMapping(request.ReplicationName, request.MappingName);
            if (mapping.ColumnMappings.Count > 0)
                return mapping.ColumnMappings;
        }

        return [.. columns.Select(c => new ColumnMapping { SourceColumn = c.Name, TargetColumn = c.Name })];
    }

    private async Task<IScriptDialect> ResolveDialectAsync(ScriptTestRequest request, CancellationToken cancellationToken)
    {
        var (source, _) = ResolveTables(request);
        var connectionName = request.ConnectionName ?? source?.ConnectionName;
        if (connectionName is null)
            return AnsiScriptDialect.Instance;

        try
        {
            var (connection, driver) = await connections.OpenAsync(connectionName, cancellationToken);
            await connection.DisposeAsync();
            return ScriptDialectAdapter.For(driver) ?? AnsiScriptDialect.Instance;
        }
        catch (Exception ex) when (ex is DbException or InvalidOperationException)
        {
            // Unreachable is not a reason to refuse a *generated* test — someone editing a script is
            // often editing it because the last run failed.
            return AnsiScriptDialect.Instance;
        }
    }

    /// <summary>
    /// What the operator is told they are looking at. A safety property, not a caption: "generated
    /// sample" and "live query against 'prod-src'" must never be confusable, because one of them
    /// touched a real system.
    /// </summary>
    private static string DescribeSource(ScriptTestRequest request, bool live, IScriptDialect dialect) =>
        live
            ? $"live query against '{request.ConnectionName}'"
            : dialect is AnsiScriptDialect
                ? "generated sample — no connection chosen, so identifiers are quoted ANSI-style"
                : $"generated sample, {dialect.EngineName} quoting";

    /// <summary>A declared parameter with no value supplied would throw on <c>Require</c>, which is
    /// not what the operator is testing. Its own name stands in, visibly.</summary>
    private static Dictionary<string, string> DefaultParameters(ScriptConfig manifest) =>
        manifest.Parameters.ToDictionary(p => p.Name, p => $"<{p.Name}>", StringComparer.OrdinalIgnoreCase);

    private static readonly IReadOnlyList<ColumnMetadata> DefaultColumns =
    [
        new ColumnMetadata("Id", "int", IsNullable: false, IsPrimaryKey: true, IsIdentity: true),
        new ColumnMetadata("Name", "nvarchar(50)", IsNullable: true, IsPrimaryKey: false, IsIdentity: false),
    ];

    private static CanonicalType CanonicalTypeOf(ColumnMetadata column) =>
        MsSqlLikeCanonical(column.NativeType);

    /// <summary>
    /// A dialect-free reading of a native type spec, for the generated path — which must work with no
    /// reachable database and therefore with no dialect to ask. Close enough to choose a sample value;
    /// the live path uses the driver's own answer.
    /// </summary>
    private static CanonicalType MsSqlLikeCanonical(string nativeType)
    {
        var (baseName, args) = CanonicalTypeSpec.Parse(nativeType);
        return baseName switch
        {
            "bit" or "boolean" or "bool" => new(CanonicalTypeKind.Boolean, null, null, null, false, false),
            "tinyint" => new(CanonicalTypeKind.Int8, null, null, null, false, false),
            "smallint" or "int2" => new(CanonicalTypeKind.Int16, null, null, null, false, false),
            "int" or "integer" or "int4" => new(CanonicalTypeKind.Int32, null, null, null, false, false),
            "bigint" or "int8" => new(CanonicalTypeKind.Int64, null, null, null, false, false),
            "decimal" or "numeric" or "money" => new(
                CanonicalTypeKind.Decimal, null, CanonicalTypeSpec.IntAt(args, 0, 18), CanonicalTypeSpec.IntAt(args, 1, 2), false, false),
            "real" or "float4" => new(CanonicalTypeKind.Float, null, null, null, false, false),
            "float" or "double" or "double precision" or "float8" => new(CanonicalTypeKind.Double, null, null, null, false, false),
            "date" => new(CanonicalTypeKind.Date, null, null, null, false, false),
            "time" => new(CanonicalTypeKind.Time, null, null, null, false, false),
            "datetime" or "datetime2" or "smalldatetime" or "timestamp" => new(CanonicalTypeKind.Timestamp, null, null, null, false, false),
            "datetimeoffset" or "timestamptz" => new(CanonicalTypeKind.TimestampTz, null, null, null, false, false),
            "uniqueidentifier" or "uuid" => new(CanonicalTypeKind.Guid, null, null, null, false, false),
            "json" or "jsonb" => new(CanonicalTypeKind.Json, null, null, null, false, false),
            "xml" => new(CanonicalTypeKind.Xml, null, null, null, false, false),
            "binary" or "varbinary" or "bytea" or "image" => new(
                CanonicalTypeKind.Binary, CanonicalTypeSpec.IntAt(args, 0, null), null, null, false,
                args.Count > 0 && args[0] == "max"),
            _ => new(
                CanonicalTypeKind.String, CanonicalTypeSpec.IntAt(args, 0, 50), null, null,
                baseName.StartsWith('n') || baseName is "text",
                args.Count > 0 && args[0] == "max"),
        };
    }

    // ---- Rendering ----

    private static string Format(object? value) => value switch
    {
        null => "NULL",
        string s => $"'{s}'",
        byte[] bytes => $"0x{Convert.ToHexString(bytes)}",
        DateTime dt => dt.ToString("O", CultureInfo.InvariantCulture),
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? "",
    };

    private static string FormatRow(ChangeRow row) =>
        "{" + string.Join(", ", row.Schema.ColumnNames.Select((name, i) => $"{name}={Format(row[i])}")) + "}";
}

/// <summary>A script that would not compile, reported the same way one that throws is.</summary>
public sealed class ScriptCompilationFailure(string message) : Exception(message);
