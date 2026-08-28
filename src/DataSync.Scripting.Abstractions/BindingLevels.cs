namespace DataSync.Scripting.Abstractions;

/// <summary>
/// The three levels a script binding can sit at, as strings — they appear in the API's slot list and in
/// the SPA, and <c>DataSync.Core.Config</c>'s own <c>BindingLevel</c> enum is not visible from here.
/// </summary>
public static class BindingLevels
{
    public const string Connection = "connection";
    public const string Replication = "replication";
    public const string Mapping = "mapping";

    public static IReadOnlyList<string> All { get; } = [Connection, Replication, Mapping];
}
