using System.Text.Json;

namespace DbDataSync.Cli;

/// <param name="Account">The account a service was registered to run as — <c>LocalSystem</c> or a
/// named account on Windows, the systemd unit's <c>User=</c> on Linux.</param>
/// <param name="Platform">"windows" or "linux" — which of <see cref="ServiceCommand"/>'s two install
/// paths wrote this.</param>
public sealed record ServiceRegistrationInfo(string Account, string Platform, DateTimeOffset RegisteredAtUtc);

/// <summary>
/// Records whether a service has ever been registered against this data directory, and under which
/// account — phase 135's own "who owns this directory" question, made durable so an ownership failure's
/// error message and <c>dbdatasync config check</c> can both answer it without an operator having to
/// remember what <c>service install</c> printed the first time they ran it.
/// <para>
/// **Deliberately not <c>dbdatasync.config.yaml</c>, and never git-committed** — same reasoning as
/// <see cref="Certificates.PendingEnrollmentStore"/>: per-machine operational state, not configuration
/// meant to be reviewed or diffed. A plain JSON file beside the config repository, gitignored the same
/// way.
/// </para>
/// </summary>
public static class ServiceRegistration
{
    private const string FileName = "service-registration.json";

    private static string PathIn(string root) => Path.Combine(root, FileName);

    public static void Write(string root, string account, string platform)
    {
        Directory.CreateDirectory(root);
        var info = new ServiceRegistrationInfo(account, platform, DateTimeOffset.UtcNow);
        File.WriteAllText(PathIn(root), JsonSerializer.Serialize(info, JsonOptions));
        EnsureGitignored(root);
    }

    public static ServiceRegistrationInfo? Read(string root)
    {
        var path = PathIn(root);
        if (!File.Exists(path))
            return null;

        var json = File.ReadAllText(path);
        if (string.IsNullOrWhiteSpace(json))
            return null;

        try
        {
            return JsonSerializer.Deserialize<ServiceRegistrationInfo>(json, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static void Clear(string root)
    {
        var path = PathIn(root);
        if (File.Exists(path))
            File.Delete(path);
    }

    /// <summary>Same reasoning as <see cref="Certificates.PendingEnrollmentStore"/>'s own — appended
    /// once, the first time this store is actually used, rather than unconditionally by
    /// <c>ServeCommand.Prepare</c>.</summary>
    private static void EnsureGitignored(string root)
    {
        var path = Path.Combine(root, ".gitignore");
        var existing = File.Exists(path) ? File.ReadAllLines(path) : [];
        if (existing.Contains(FileName))
            return;

        using var writer = new StreamWriter(path, append: true);
        if (existing.Length > 0 && !string.IsNullOrEmpty(existing[^1]))
            writer.WriteLine();
        writer.WriteLine(FileName);
    }

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
}
