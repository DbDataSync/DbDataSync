using DataSync.Core.Config;
using DataSync.Drivers.Abstractions;

namespace DataSync.Api.Services;

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
        var capabilities = driverRegistry.Describe(input.DriverType);
        if (capabilities is null)
            return;

        Throw(ParameterValidation.Validate(
            capabilities.ConnectionParameters, Flatten("properties", input.Properties), $"Connection '{input.Name}'"));
    }

    /// <summary>
    /// A replication's three stages, against whichever driver its **source** connection uses — which
    /// is the one that resolves reader Kinds. A replication whose endpoints are not set yet is not
    /// checked: it cannot name a driver, and refusing to save it would mean the endpoints could never
    /// be filled in.
    /// </summary>
    public void ThrowIfInvalid(ReplicationTaskConfig task)
    {
        if (ResolveDriver(task) is not { } capabilities)
            return;

        var processing = task.ChangeProcessing;
        var problems = new List<string>();

        problems.AddRange(Check(
            capabilities.Readers.FirstOrDefault(r => r.Kind == processing.Reader.Kind)?.Parameters,
            processing.Reader.Options, $"Reader '{processing.Reader.Kind}'"));

        problems.AddRange(Check(
            capabilities.StagingProviders.FirstOrDefault(s => s.Kind == processing.Cache.Kind)?.Parameters,
            processing.Cache.Options, $"Staging '{processing.Cache.Kind}'"));

        problems.AddRange(Check(
            capabilities.Writers.FirstOrDefault(w => w.Kind == processing.Writer.Kind)?.Parameters,
            processing.Writer.Options, $"Writer '{processing.Writer.Kind}'"));

        Throw(problems);
    }

    /// <summary>
    /// A Kind this driver does not offer is not this check's business — the pipeline says so at run
    /// time, with a better message, and reporting it twice from two places would be two things to keep
    /// in step.
    /// </summary>
    private static IReadOnlyList<string> Check(
        IReadOnlyList<ParameterDescriptor>? declared, Dictionary<string, string> values, string what) =>
        declared is null ? [] : ParameterValidation.Validate(declared, values, what);

    private DriverCapabilities? ResolveDriver(ReplicationTaskConfig task)
    {
        var connectionName = task.Endpoints?.Source?.ConnectionName;
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
