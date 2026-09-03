namespace DbDataSync.Drivers.Abstractions;

/// <summary>
/// The stages a preview groups statements under, in the order a pass runs them. Constants rather than
/// an enum because they are labels the SPA renders and a driver may need one of its own; the order is
/// what <see cref="PreviewStatement"/> lists are sorted into.
/// </summary>
public static class PreviewStages
{
    public const string BeforeStage = "Before staging";
    public const string SourceRead = "Source read";
    public const string Staging = "Staging";
    public const string AfterStage = "After staging";
    public const string BeforeLoad = "Before load";
    public const string Write = "Write";
    public const string AfterLoad = "After load";

    /// <summary>The order a pass runs them in, which is the order the preview lists them in.</summary>
    public static IReadOnlyList<string> InOrder { get; } =
        [BeforeStage, SourceRead, Staging, AfterStage, BeforeLoad, Write, AfterLoad];
}
