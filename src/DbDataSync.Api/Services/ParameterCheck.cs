using DbDataSync.Core.Config;
using DbDataSync.Drivers.Abstractions;

namespace DbDataSync.Api.Services;

/// <summary>
/// Checks the settings on a saved connection or replication against what the driver declared.
/// <para>
/// Here rather than in <c>ConfigRepository</c> because the declarations live on the drivers, and Core
/// does not — and must not — see the driver layer. This is the seam where both are in scope.
/// </para>
/// <para>
/// Every problem at once, then one rejection. Somebody filling in six settings should find out about
/// all six mistakes on one save.
/// </para>
/// </summary>
public sealed class ParameterCheck(DriverRegistry driverRegistry, ConfigRepository configRepository)
{
    public void ThrowIfInvalid(ConnectionInput input)
    {
        if (!driverRegistry.TryGet(input.DriverType, out var driver))
            return;

        var values = DriverParameters.ValuesOf(input);

        // Checked against the declarations *as they apply to this connection*: a host that is not a
        // setting in connection-string mode is not one this can complain about either. Which of the
        // two addressing modes is filled in is `ConfigValidation.ValidateAddressing`'s question, and
        // it asks it with a better message than a generic "required" would.
        Throw(ParameterValidation.Validate(
            driver.ConnectionParameters(values), values, $"Connection '{input.Name}'"));
    }

    /// <summary>
    /// A replication's three stages: the reader against its **source** connection's driver, staging and
    /// the writer against its **target's**. A replication whose endpoints are not set yet is not
    /// checked on the side it has not named: it cannot name a driver there, and refusing to save it
    /// would mean the endpoints could never be filled in.
    /// <para>
    /// This checked all three against the source until phase 68 — harmless while reader/staging/writer
    /// Kinds mostly existed identically on both registered drivers, and wrong in principle the whole
    /// time. Corrected here rather than left beside the newly-correct per-mapping check below saying
    /// something different about the same question.
    /// </para>
    /// </summary>
    public void ThrowIfInvalid(ReplicationTaskConfig task)
    {
        var problems = new List<string>();
        Check(
            ResolveDriver(task.Endpoints?.Source?.ConnectionName),
            ResolveDriver(task.Endpoints?.Target?.ConnectionName),
            task.ChangeProcessing.Reader, task.ChangeProcessing.Cache, task.ChangeProcessing.Writer,
            prefix: "", requireKnownKind: false, problems);

        Throw(problems);
    }

    /// <summary>
    /// A table mapping's per-stage overrides, each against the driver that actually resolves it —
    /// <see cref="TableMappingConfig.ReaderOverride"/> against the source's, the other two against the
    /// target's, the way <c>RunExecutor</c> itself resolves them. Null overrides are checked against
    /// nothing, because a stage that inherits was already checked where it is stated.
    /// <para>
    /// **A Kind the driver does not offer is an error here**, unlike at the replication level above.
    /// An override exists only to name something different from what would otherwise run; naming
    /// something that cannot run makes the mapping silently unrunnable, and the save is the moment
    /// somebody is still looking at it.
    /// </para>
    /// </summary>
    public void ThrowIfInvalid(ReplicationTaskConfig task, TableMappingConfig mapping)
    {
        if (mapping.ReaderOverride is null && mapping.CacheOverride is null && mapping.WriterOverride is null)
            return;

        var problems = new List<string>();
        Check(
            ResolveDriver(ConnectionOf(() => EndpointResolution.ResolveSource(task, mapping.Sources[0]).ConnectionName,
                task.Endpoints?.Source?.ConnectionName, mapping.Sources.Count)),
            ResolveDriver(ConnectionOf(() => EndpointResolution.ResolveTarget(task, mapping.Targets[0]).ConnectionName,
                task.Endpoints?.Target?.ConnectionName, mapping.Targets.Count)),
            mapping.ReaderOverride, mapping.CacheOverride, mapping.WriterOverride,
            prefix: $"'{mapping.Name}' ", requireKnownKind: true, problems);

        Throw(problems);
    }

    /// <summary>
    /// The connection a side of this mapping resolves to — its own, if it overrides the replication's
    /// endpoint (phase 16), and the replication's otherwise.
    /// <para>
    /// Falls back rather than throwing on a mapping that does not resolve yet: that is
    /// <c>EndpointResolution.Validate</c>'s error to report, with a message about endpoints rather than
    /// about parameters, and it runs a moment later in <c>SaveTableMapping</c>.
    /// </para>
    /// </summary>
    private static string? ConnectionOf(Func<string> resolve, string? fallback, int sideCount)
    {
        if (sideCount != 1)
            return fallback;

        try
        {
            return resolve();
        }
        catch (ConfigValidationException)
        {
            return fallback;
        }
    }

    /// <summary>The three stages, each against the capabilities of the side that runs it. A stage that
    /// is null is one this caller has nothing to say about.</summary>
    private static void Check(
        DriverCapabilities? source, DriverCapabilities? target,
        ReaderConfig? reader, CacheConfig? cache, WriterConfig? writer,
        string prefix, bool requireKnownKind, List<string> problems)
    {
        if (source is not null && reader is not null)
            problems.AddRange(Check(
                source.Readers.FirstOrDefault(r => r.Kind == reader.Kind)?.Parameters,
                reader.Options, $"{prefix}Reader '{reader.Kind}'", requireKnownKind));

        if (target is null)
            return;

        if (cache is not null)
            problems.AddRange(Check(
                target.StagingProviders.FirstOrDefault(s => s.Kind == cache.Kind)?.Parameters,
                cache.Options, $"{prefix}Staging '{cache.Kind}'", requireKnownKind));

        if (writer is not null)
            problems.AddRange(Check(
                target.Writers.FirstOrDefault(w => w.Kind == writer.Kind)?.Parameters,
                writer.Options, $"{prefix}Writer '{writer.Kind}'", requireKnownKind));
    }

    /// <summary>
    /// A Kind the driver does not offer is left to run time for a replication — the pipeline says so
    /// there, with a better message, and reporting it twice from two places would be two things to keep
    /// in step. For a mapping override it is refused outright; see the caller.
    /// </summary>
    private static IReadOnlyList<string> Check(
        IReadOnlyList<ParameterDescriptor>? declared, Dictionary<string, string> values, string what,
        bool requireKnownKind) =>
        declared is not null ? ParameterValidation.Validate(declared, values, what)
        : requireKnownKind ? [$"{what}: the connection's driver does not offer that."]
        : [];

    private DriverCapabilities? ResolveDriver(string? connectionName)
    {
        if (string.IsNullOrWhiteSpace(connectionName))
            return null;

        try
        {
            return driverRegistry.Describe(configRepository.LoadConnection(connectionName).DriverType);
        }
        catch (FileNotFoundException)
        {
            // A replication naming a connection that does not exist is a separate error with its own
            // message; this check has nothing to add to it.
            return null;
        }
    }

    /// <summary>
    /// A free-form bag arrives as its own dictionary rather than under the declared parameter's name,
    /// so it is keyed the way <see cref="ParameterValidation"/> expects a vararg to be before it is
    /// checked. The persisted shape is unchanged — this is about what the validator is looking at.
    /// </summary>
    private static Dictionary<string, string> Flatten(string parameterName, Dictionary<string, string> bag) =>
        bag.ToDictionary(e => ParameterValidation.KeyFor(new ParameterDescriptor { Name = parameterName }, e.Key), e => e.Value);

    private static void Throw(IReadOnlyList<string> problems)
    {
        if (problems.Count > 0)
            throw new ConfigValidationException(string.Join(" ", problems));
    }
}
