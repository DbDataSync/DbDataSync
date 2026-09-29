namespace DbDataSync.Updates;

/// <summary>
/// The commands an operator types to update this installation — shown by the web console, which cannot apply an
/// update itself (phase 196L), and quoted by the CLI. Written down once so the two say the same thing.
/// </summary>
/// <param name="Where">Where to run them, in a sentence: which shell, and with what privilege.</param>
/// <param name="List">Lists the releases on every channel. Needs no privilege.</param>
/// <param name="Status">Slots, the last update, and its history. Needs no privilege.</param>
/// <param name="Apply">Installs and switches to a version: <see cref="VersionPlaceholder"/> stands for it.</param>
/// <param name="Rollback">Switches back to the other slot.</param>
public sealed record UpdateCliCommands(string Where, string List, string Status, string Apply, string Rollback)
{
    public const string VersionPlaceholder = "{version}";

    public string ApplyFor(string version) => Apply.Replace(VersionPlaceholder, version, StringComparison.Ordinal);

    /// <param name="repoRoot">The data directory, when it is not the default — then every command that reads it says
    /// which. Null for the default.</param>
    public static UpdateCliCommands For(bool windows, string? repoRoot)
    {
        var repo = repoRoot is null ? "" : " --repo " + Quote(repoRoot);
        var elevated = windows ? "" : "sudo ";
        return new UpdateCliCommands(
            windows
                ? "In PowerShell on the server, started with Run as administrator — applying stops and starts the service and writes the install directory."
                : "In a terminal on the server. Applying and rolling back need root, because they stop and start the service and write the install directory.",
            "dbdatasync update --list",
            "dbdatasync update --status" + repo,
            $"{elevated}dbdatasync update --to {VersionPlaceholder} --apply" + repo,
            $"{elevated}dbdatasync update --rollback" + repo);
    }

    private static string Quote(string value) =>
        value.IndexOfAny([' ', '\t', '&', '(', ')', '$', ';', '|', '<', '>', '\'']) >= 0 ? $"\"{value}\"" : value;
}
