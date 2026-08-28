using DataSync.Core.Config;

namespace DataSync.Drivers.Abstractions;

/// <summary>Parameter declarations shared across drivers, so the same setting is not described three
/// slightly different ways.</summary>
public static class DriverParameters
{
    /// <summary>
    /// The free-form bag appended to a connection string. Every driver has had one since phase 3;
    /// declaring it here is what turns it from something the connection screen assumes into something
    /// a driver states — and therefore something a driver could narrow, or drop, or replace with
    /// named settings of its own.
    /// </summary>
    public static ParameterDescriptor ConnectionProperties { get; } = new()
    {
        Name = "properties",
        Label = "Custom properties",
        Description = "Appended to the connection string. Engine-specific settings this driver does not name.",
        Type = ParameterType.Property,
        Cardinality = ParameterCardinality.Any,
        Layout = new ParameterLayout(Card: "Custom properties"),
    };
}
