namespace DbDataSync.Api;

/// <summary>
/// Where the web console's files are (phase 160). ASP.NET looks for <c>wwwroot</c> under the process's
/// <em>current directory</em>, which is the tool's own directory only by accident: a terminal <c>dbdatasync serve</c> runs
/// wherever the user is, and the systemd unit sets <c>WorkingDirectory</c> to the data root. Neither has a <c>wwwroot</c>,
/// so an installed tool answered <c>404 "No web assets are published"</c> to <c>/</c> while its own directory held the
/// whole console. (The Windows service escapes it — <c>UseWindowsService</c> moves the content root to the app
/// directory — and so does the container, whose <c>WORKDIR</c> is the app directory; which is why it went unseen.)
/// </summary>
public static class WebRootLocator
{
    /// <summary>The app's own <c>wwwroot</c>, when the working directory has none of its own to use; otherwise null, which
    /// leaves ASP.NET's default in place. A <c>wwwroot</c> in the working directory wins, so running the API from a source
    /// checkout — or a container, where the two are the same directory — is unchanged.</summary>
    public static string? Resolve(string currentDirectory, string appDirectory)
    {
        var inCurrent = Path.Combine(currentDirectory, "wwwroot");
        var inApp = Path.Combine(appDirectory, "wwwroot");

        return !Directory.Exists(inCurrent) && Directory.Exists(inApp) ? inApp : null;
    }
}
