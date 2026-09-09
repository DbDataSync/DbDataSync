namespace DbDataSync.Api.Services;

/// <summary>Shapes a failed <c>LibraryInstaller.InstallAsync</c>'s exception message — which already
/// carries `dotnet publish`'s full stdout+stderr (see <c>LibraryInstaller.PublishAsync</c>) — into
/// something worth putting in an HTTP response body: the tail, not the whole restore log, which for a
/// large dependency closure can run to hundreds of lines.</summary>
public static class InstallErrorFormatting
{
    private const int MaxLines = 20;

    public static string TailOf(string message)
    {
        var lines = message.Split('\n');
        return lines.Length <= MaxLines ? message : string.Join('\n', lines[^MaxLines..]);
    }
}
