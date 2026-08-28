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
                ScriptSlots.LifecycleHook => TestLifecycleHook(
                    compiled.CreateInstance<ILifecycleHook>(manifest.Name),
                    mappings, columns, dialect, manifest, log, request),
                ScriptSlots.SourceQueryBuilder => TestSourceQueryBuilder(
                    compiled.CreateInstance<ISourceQueryBuilder>(manifest.Name),
                    mappings, columns, dialect, manifest, request),
                ScriptSlots.VerificationQueryBuilder => TestVerificationQueryBuilder(
                    compiled.CreateInstance<IVerificationQueryBuilder>(manifest.Name),
                    mappings, columns, dialect, manifest, request),
                ScriptSlots.MetadataProvider => await TestMetadataProviderAsync(
                    compiled.CreateInstance<IMetadataProvider>(manifest.Name),
                    dialect, manifest, request, live, cancellationToken),
                _ => throw new ConfigValidationException(
                    $"'{manifest.Kind}' is not a slot this build knows how to test."),
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

    /// <summary>
    /// Which points the hook wants, and the statements it would emit at each. Nothing is executed —
    /// a lifecycle hook returns a description and the host runs it, so the description *is* the thing
    /// worth checking.
    /// </summary>
    private IReadOnlyList<ScriptTestCase> TestLifecycleHook(
        ILifecycleHook script, IReadOnlyList<ColumnMapping> mappings, IReadOnlyList<ColumnMetadata> columns,
        IScriptDialect dialect, ScriptConfig manifest, List<string> log, ScriptTestRequest request)
    {
        var (source, target) = ResolveTables(request);
        var context = new LifecycleHookContext(
            "(declare)",
            source ?? SampleSource, target ?? SampleTarget, mappings, columns, columns,
            dialect, dialect,
            // The facts a pass would have. Nulls where a real pass has them before the work happens,
            // because a hook that branches on RowsWritten needs to see that case too.
            new HookRunFacts(
                Guid.Empty, request.ReplicationName ?? "(replication)", request.MappingName ?? "(mapping)",
                "Primary", Segment: null, SegmentIndex: 0, SegmentCount: 1, IsLastSegment: true,
                RowsStaged: null, RowsWritten: null, StagingLocation: null, Watermark: null),
            new ScriptParameters(DefaultParameters(manifest)), log.Add);

        var points = script.DeclarePoints(context);
        if (points.Count == 0)
            return [new ScriptTestCase("(declares no points)", null, "This hook would never run as configured.")];

        var cases = new List<ScriptTestCase>();
        foreach (var point in points)
        {
            var statements = script.BuildStatements(point, context with { Point = point });
            if (statements.Count == 0)
            {
                cases.Add(new ScriptTestCase(point, null, "declared, but emits nothing for these facts"));
                continue;
            }

            foreach (var statement in statements)
            {
                var parameters = statement.Parameters.Count == 0
                    ? null
                    : string.Join(", ", statement.Parameters.Select(p => $"{p.Name}={Format(p.Value)}"));
                cases.Add(new ScriptTestCase(point, statement.CommandText, parameters));
            }
        }
        return cases;
    }

    /// <summary>
    /// The statements a query builder would issue, and how it says to read the result back. Not
    /// executed: this slot owns a source read outright, and running one to see what it does is the
    /// thing an operator should choose deliberately rather than get from a button called Test.
    /// </summary>
    private IReadOnlyList<ScriptTestCase> TestSourceQueryBuilder(
        ISourceQueryBuilder script, IReadOnlyList<ColumnMapping> mappings, IReadOnlyList<ColumnMetadata> columns,
        IScriptDialect dialect, ScriptConfig manifest, ScriptTestRequest request)
    {
        var (source, _) = ResolveTables(request);
        var parameters = new ScriptParameters(DefaultParameters(manifest));

        // A previous watermark, because the statement that matters is the incremental one — a builder
        // shown only its first-pass form is a builder half checked.
        var context = new SourceQueryContext(
            source ?? SampleSource, mappings, columns, PreviousWatermark: "1000", EndWatermark: null,
            Segment: null, dialect, parameters);

        var cases = new List<ScriptTestCase>();

        var watermarkQuery = script.BuildWatermarkQuery(context);
        cases.Add(new ScriptTestCase(
            "Watermark query", watermarkQuery?.CommandText,
            watermarkQuery is null
                ? "none — the host echoes the previous watermark back rather than inventing one"
                : Describe(watermarkQuery.Parameters)));

        var readQuery = script.BuildReadQuery(context with { EndWatermark = "2000" });
        cases.Add(new ScriptTestCase("Read query", readQuery.CommandText, Describe(readQuery.Parameters)));

        var shape = script.DescribeResult(context);
        cases.Add(new ScriptTestCase(
            "Result shape",
            shape.OperationColumn is null
                ? $"every row is a {shape.DefaultOperation}"
                : $"operation from '{shape.OperationColumn}'",
            shape.ExcludeColumns is { Count: > 0 }
                ? $"dropped before staging: {string.Join(", ", shape.ExcludeColumns)}"
                : null));

        return cases;

        static string? Describe(IReadOnlyList<ScriptQueryParameter> parameters) =>
            parameters.Count == 0 ? null : string.Join(", ", parameters.Select(p => $"{p.Name}={Format(p.Value)}"));
    }

    /// <summary>
    /// The one slot whose contract hands the script a live <see cref="DbConnection"/>, because there
    /// is no way to describe "ask the catalog" as data. So there is no generated mode for it: a
    /// generated one would have to pass a connection that is not open, and a script doing the normal
    /// thing with it would fail for a reason that has nothing to do with the script.
    /// <para>
    /// Testing it means choosing a connection, which is exactly what its own contract already means.
    /// </para>
    /// </summary>
    private async Task<IReadOnlyList<ScriptTestCase>> TestMetadataProviderAsync(
        IMetadataProvider script, IScriptDialect dialect, ScriptConfig manifest,
        ScriptTestRequest request, bool live, CancellationToken cancellationToken)
    {
        if (!live)
        {
            throw new ConfigValidationException(
                "A catalog provider is handed a live connection by contract, so testing one means " +
                "choosing a connection — there is nothing to generate.");
        }

        var (connection, driver) = await connections.OpenAsync(request.ConnectionName!, cancellationToken);
        try
        {
            var context = new MetadataContext(
                connection, dialect, new ScriptParameters(DefaultParameters(manifest)),
                ct => driver.ListDatabasesAsync(connection, ct),
                (database, ct) => driver.ListTablesAsync(connection, database, ct),
                (database, schema, table, ct) => driver.ListColumnsAsync(connection, database, schema, table, ct));

            var databases = await script.ListDatabasesAsync(context, cancellationToken);
            var cases = new List<ScriptTestCase>
            {
                new($"{databases.Count} database(s)", string.Join(", ", databases.Take(20)),
                    databases.Count > 20 ? "first 20 shown" : null),
            };

            // One database deep, not all of them: the point is to see the script answer, not to walk
            // a catalog that may be enormous.
            if (databases.Count > 0)
            {
                var tables = await script.ListTablesAsync(context, databases[0], cancellationToken);
                cases.Add(new ScriptTestCase(
                    $"{tables.Count} table(s) in {databases[0]}",
                    string.Join(", ", tables.Take(20).Select(t => $"{t.Schema}.{t.Table}")),
                    tables.Count > 20 ? "first 20 shown" : null));

                if (tables.Count > 0)
                {
                    var first = tables[0];
                    var columns = await script.ListColumnsAsync(
                        context, databases[0], first.Schema, first.Table, cancellationToken);
                    cases.Add(new ScriptTestCase(
                        $"{columns.Count} column(s) on {first.Schema}.{first.Table}",
                        string.Join(", ", columns.Take(20).Select(c => $"{c.Name} {c.NativeType}")),
                        columns.Count > 20 ? "first 20 shown" : null));
                }
            }

            return cases;
        }
        finally
        {
            await connection.DisposeAsync();
        }
    }

    /// <summary>
    /// Both sides' statements and the shape it says they come back in — described, not run. A
    /// verification query reads from live tables, and running one because a button says Test is the
    /// kind of thing an operator should choose deliberately rather than have chosen for them.
    /// <para>
    /// Asked once per side, which is the point of the contract: a builder that answers the same for
    /// both is a builder that could have been a generic SQL check, and one that does not is the reason
    /// this slot exists.
    /// </para>
    /// </summary>
    private IReadOnlyList<ScriptTestCase> TestVerificationQueryBuilder(
        IVerificationQueryBuilder script, IReadOnlyList<ColumnMapping> mappings,
        IReadOnlyList<ColumnMetadata> columns, IScriptDialect dialect, ScriptConfig manifest,
        ScriptTestRequest request)
    {
        var (source, target) = ResolveTables(request);
        var parameters = new ScriptParameters(DefaultParameters(manifest));

        VerificationQueryContext Context(VerificationSideKind side, TableRef? table) => new(
            side, table ?? (side == VerificationSideKind.Source ? SampleSource : SampleTarget),
            mappings, columns, Filter: null, dialect, parameters);

        var sourceContext = Context(VerificationSideKind.Source, source);
        var targetContext = Context(VerificationSideKind.Target, target);

        var shape = script.DescribeResult(sourceContext);

        return
        [
            new ScriptTestCase("Source query", script.BuildQuery(sourceContext).CommandText),
            new ScriptTestCase("Target query", script.BuildQuery(targetContext).CommandText),
            new ScriptTestCase(
                "Result shape",
                $"grouped by {Join(shape.GroupColumns)}; measuring {Join(shape.MeasureColumns)}",
                shape.GroupColumns.Count == 0 && shape.MeasureColumns.Count == 0
                    ? "Nothing to compare — a check with no measures reports no differences."
                    : null),
        ];

        static string Join(IReadOnlyList<string> names) => names.Count == 0 ? "(none)" : string.Join(", ", names);
    }

    /// <summary>Stand-ins for a script not yet bound to a mapping, so it can be tested the moment it
    /// is written.</summary>
    private static SourceTableRef SampleSource { get; } =
        new() { ConnectionName = "(source)", Database = "(database)", Schema = "dbo", Table = "SampleTable" };

    private static TableRef SampleTarget { get; } =
        new() { ConnectionName = "(target)", Database = "(database)", Schema = "dbo", Table = "SampleTable" };

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
