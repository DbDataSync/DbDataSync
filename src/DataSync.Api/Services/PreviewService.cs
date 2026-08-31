using System.Data.Common;
using DataSync.Core.Config;
using DataSync.Drivers.Abstractions;
using DataSync.Drivers.Generic;
using DataSync.Scripting;
using DataSync.Scripting.Abstractions;
using DataSync.State;
using DataSync.Core.Sql;

namespace DataSync.Api.Services;

/// <summary>Everything a pass would do, in the order it would do it.</summary>
public sealed record PreviewReport(
    IReadOnlyList<PreviewStatement> Statements,
    IReadOnlyList<string> Problems);

/// <summary>
/// Assembles the preview of one table mapping: every statement a pass would run, plus the in-process
/// steps that generate none, each labelled with where it came from.
/// <para>
/// It asks the pipeline components what they would run rather than reconstructing it. That is the
/// difference between a preview and a plausible guess — see <see cref="IStatementPreview"/>. What
/// this class owns is the parts that are not any one component's: the hooks, the scripted transforms,
/// and the order.
/// </para>
/// <para>
/// The read is described against the **stored watermark**, so an incremental reader shows the
/// statement it would issue next rather than a first-run one. Anything else would be a preview that
/// is correct exactly once.
/// </para>
/// </summary>
public sealed class PreviewService(
    ConfigRepository configRepository,
    DriverConnectionFactory connections,
    DriverRegistry driverRegistry,
    ScriptHost scriptHost,
    ChangeWatermarkStore watermarks)
{
    public async Task<PreviewReport> BuildAsync(
        string replicationName, string mappingName, CancellationToken cancellationToken)
    {
        var task = configRepository.LoadReplicationTask(replicationName);
        var mapping = configRepository.LoadTableMapping(replicationName, mappingName);

        if (mapping.Sources.Count != 1 || mapping.Targets.Count != 1)
            throw new ConfigValidationException(
                $"Table mapping '{mapping.Name}' has {mapping.Sources.Count} source(s) and " +
                $"{mapping.Targets.Count} target(s) — preview describes 1:1 mappings.");

        var source = EndpointResolution.ResolveSource(task, mapping.Sources[0]);
        var target = EndpointResolution.ResolveTarget(task, mapping.Targets[0]);

        var statements = new List<PreviewStatement>();
        var problems = new List<string>();

        DbConnection? sourceConnection = null;
        DbConnection? targetConnection = null;
        try
        {
            (sourceConnection, var sourceDriver) = await connections.OpenAsync(source.ConnectionName, cancellationToken);
            (targetConnection, var targetDriver) = await connections.OpenAsync(target.ConnectionName, cancellationToken);

            var sourceConnectionConfig = configRepository.LoadConnection(source.ConnectionName);
            var targetConnectionConfig = configRepository.LoadConnection(target.ConnectionName);
            var sourceDialect = DialectOf(sourceDriver);
            var targetDialect = DialectOf(targetDriver);

            // The same substitution a pass makes before the reader ever sees the mappings: a scripted
            // column expression becomes a literal Transform, so what the projection renders here is
            // what it would render then.
            var columnMappings = ApplyScriptedTransforms(
                task, mapping, sourceConnectionConfig, sourceDriver, statements, problems);

            statements.AddRange(DescribeInProcessTransforms(task, mapping, sourceConnectionConfig));
            statements.AddRange(RenderHooks(
                task, mapping, targetConnectionConfig, source, target, sourceDialect, targetDialect, problems));

            var processing = task.ChangeProcessing;
            var previousWatermark = watermarks.GetWatermark(task.Name, WatermarkKey.Build(source));

            await DescribeAsync(
                driverRegistry.FindReader(sourceDriver.DriverType, processing.Reader.Kind),
                $"reader '{processing.Reader.Kind}'", PreviewStages.SourceRead,
                new PreviewRequest(sourceConnection, source, target, columnMappings, processing.Reader.Options, previousWatermark),
                statements, problems, cancellationToken);

            await DescribeAsync(
                targetDriver.StagingProviders.FirstOrDefault(p => p.Kind == processing.Cache.Kind),
                $"staging provider '{processing.Cache.Kind}'", PreviewStages.Staging,
                new PreviewRequest(targetConnection, source, target, columnMappings, processing.Cache.Options, previousWatermark),
                statements, problems, cancellationToken);

            await DescribeAsync(
                targetDriver.Writers.FirstOrDefault(w => w.Kind == processing.Writer.Kind),
                $"writer '{processing.Writer.Kind}'", PreviewStages.Write,
                new PreviewRequest(targetConnection, source, target, columnMappings, processing.Writer.Options, previousWatermark),
                statements, problems, cancellationToken);
        }
        finally
        {
            if (sourceConnection is not null) await sourceConnection.DisposeAsync();
            if (targetConnection is not null) await targetConnection.DisposeAsync();
        }

        // Stable within a stage, ordered across them — the order a pass runs, which is the only order
        // that makes the list readable as a description of what happens.
        var ordered = statements
            .OrderBy(s => PreviewStages.InOrder.ToList().IndexOf(s.Stage) is var i && i >= 0 ? i : int.MaxValue)
            .ToList();

        return new PreviewReport(ordered, problems);
    }

    /// <summary>
    /// A component that cannot describe itself is reported rather than omitted. Silence would read as
    /// "this stage runs nothing", which is the one thing it definitely does not mean.
    /// </summary>
    private static async Task DescribeAsync(
        object? component, string what, string stage, PreviewRequest request,
        List<PreviewStatement> statements, List<string> problems, CancellationToken cancellationToken)
    {
        if (component is null)
        {
            problems.Add($"The configured {what} is not available on this driver, so this pass would not run.");
            return;
        }

        if (component is not IStatementPreview describable)
        {
            statements.Add(new PreviewStatement(
                stage, what, null, PreviewOrigin.BuiltIn,
                "This component does not describe its statements, so what it runs cannot be shown here."));
            return;
        }

        try
        {
            statements.AddRange(await describable.DescribeAsync(request, cancellationToken));
        }
        catch (Exception ex) when (ex is DbException or InvalidOperationException)
        {
            statements.Add(new PreviewStatement(stage, what, null, PreviewOrigin.BuiltIn, ex.Message));
        }
    }

    /// <summary>
    /// Materialises a scripted column expression into the literal <see cref="ColumnMapping.Transform"/>
    /// it becomes, exactly as a pass does — and lists each generated expression, because "where did
    /// that SQL come from" is the question a preview exists to answer.
    /// </summary>
    private IReadOnlyList<ColumnMapping> ApplyScriptedTransforms(
        ReplicationTaskConfig task, TableMappingConfig mapping, ConnectionConfig sourceConnection,
        IDriver sourceDriver, List<PreviewStatement> statements, List<string> problems)
    {
        var binding = ScriptResolution.Resolve(ScriptSlots.SqlColumnExpression, sourceConnection, task, mapping);
        if (binding is null)
            return mapping.ColumnMappings;

        var level = ScriptResolution.LevelOf(ScriptSlots.SqlColumnExpression, sourceConnection, task, mapping);
        try
        {
            var resolved = scriptHost.ResolveBinding<ISqlColumnExpression>(
                ScriptSlots.SqlColumnExpression, sourceConnection, task, mapping)!.Value;

            var generated = new List<string>();
            var result = ScriptedColumnTransforms.Apply(
                mapping.ColumnMappings, resolved.Script, resolved.Parameters,
                ScriptDialectAdapter.For(sourceDriver) ?? DialectlessScriptDialect.Instance,
                columnMetadata: null, log: generated.Add);

            foreach (var expression in generated)
            {
                statements.Add(new PreviewStatement(
                    PreviewStages.SourceRead, $"Generated column expression: {expression}", null,
                    PreviewOrigin.Script, $"Script '{binding.ScriptName}', bound on the {level.ToString().ToLowerInvariant()}."));
            }

            if (generated.Count == 0)
            {
                statements.Add(new PreviewStatement(
                    PreviewStages.SourceRead, "Column-expression script generated nothing for this mapping", null,
                    PreviewOrigin.Script, $"Script '{binding.ScriptName}', bound on the {level.ToString().ToLowerInvariant()}."));
            }

            return result;
        }
        catch (Exception ex) when (ex is ScriptExecutionException or InvalidOperationException)
        {
            problems.Add(
                $"The column-expression script '{binding.ScriptName}' could not run, so the source read below " +
                $"is shown without whatever it would have generated: {ex.Message}");
            return mapping.ColumnMappings;
        }
    }

    /// <summary>
    /// The in-process transforms, named rather than shown. They are C# running over rows and generate
    /// no SQL; inventing a statement for one would be worse than saying it has none.
    /// </summary>
    private static IEnumerable<PreviewStatement> DescribeInProcessTransforms(
        ReplicationTaskConfig task, TableMappingConfig mapping, ConnectionConfig sourceConnection)
    {
        foreach (var slot in new[] { ScriptSlots.ValueColumnExpression, ScriptSlots.RowTransform })
        {
            if (ScriptResolution.Resolve(slot, sourceConnection, task, mapping) is not { } binding)
                continue;

            var level = ScriptResolution.LevelOf(slot, sourceConnection, task, mapping);
            yield return new PreviewStatement(
                PreviewStages.SourceRead, $"{ScriptSlots.Describe(slot).Label}: '{binding.ScriptName}'", null,
                PreviewOrigin.Script,
                $"Runs in this process on every row between the reader and staging, bound on the " +
                $"{level.ToString().ToLowerInvariant()}. No SQL — see the script itself.");
        }
    }

    /// <summary>
    /// Every configured hook, rendered through the same <see cref="HookRenderer"/> a pass uses, with a
    /// context carrying what is known before a run: no run id, no row counts, and a staging name that
    /// does not exist yet.
    /// </summary>
    private IEnumerable<PreviewStatement> RenderHooks(
        ReplicationTaskConfig task, TableMappingConfig mapping, ConnectionConfig targetConnectionConfig,
        SourceTableRef source, TableRef target, SqlDialect sourceDialect, SqlDialect targetDialect,
        List<string> problems)
    {
        var context = new HookRenderContext(
            targetDialect.QualifyTable(target.Schema, target.Table),
            targetDialect.QuoteIdentifier(target.Schema),
            targetDialect.QuoteIdentifier(target.Table),
            sourceDialect.QualifyTable(source.Schema, source.Table),
            StagingQualified: "<staging>",
            task.Name, mapping.Name, Guid.Empty, RunKind.Primary.ToString(),
            Segment: null, SegmentIndex: 0, SegmentCount: 1, IsLastSegment: true,
            RowsStaged: null, RowsWritten: null, Watermark: null);

        var stageByPoint = new Dictionary<string, string>
        {
            [HookPoints.BeforeStage] = PreviewStages.BeforeStage,
            [HookPoints.AfterStage] = PreviewStages.AfterStage,
            [HookPoints.BeforeLoad] = PreviewStages.BeforeLoad,
            [HookPoints.AfterLoad] = PreviewStages.AfterLoad,
        };

        foreach (var point in HookPoints.All)
        {
            var hooks = HookResolution.Resolve(point, targetConnectionConfig, task, mapping);
            foreach (var hook in hooks ?? [])
            {
                var label = hook.Name ?? hook.Hook ?? "inline";
                var dialect = hook.Connection == HookConnectionSide.Source ? sourceDialect : targetDialect;
                string? sql = null;
                string? detail;
                try
                {
                    var (body, parameters) = hook.Sql is { } inline
                        ? (inline, (IReadOnlyDictionary<string, string>)new Dictionary<string, string>())
                        : (configRepository.LoadScript(hook.Hook!).Code, hook.Parameters);
                    sql = HookRenderer.Render(dialect, body, parameters, context).CommandText;
                    detail = $"Runs against the {hook.Connection.ToString().ToLowerInvariant()}.";
                }
                catch (FileNotFoundException)
                {
                    detail = $"Hook script '{hook.Hook}' was not found.";
                    problems.Add($"The {point} hook '{label}' names a script that does not exist.");
                }

                yield return new PreviewStatement(
                    stageByPoint[point], $"Hook: {label}", sql,
                    hook.Sql is not null ? PreviewOrigin.OperatorSql : PreviewOrigin.Script, detail);
            }
        }
    }

    /// <summary>A driver names its own dialect (<see cref="IDialectProvider"/>) rather than a switch
    /// here having to know every engine.</summary>
    private static SqlDialect DialectOf(IDriver driver) =>
        driver is IDialectProvider provider
            ? provider.Dialect
            : throw new InvalidOperationException($"The '{driver.DriverType}' driver does not name a SQL dialect.");
}
