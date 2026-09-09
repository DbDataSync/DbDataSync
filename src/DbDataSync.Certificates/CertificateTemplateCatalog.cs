using System.DirectoryServices;
using System.DirectoryServices.ActiveDirectory;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace DbDataSync.Certificates;

/// <summary>Why <see cref="CertificateTemplateCatalog.List"/> came back empty — <see
/// cref="Available"/> is the one case it didn't.</summary>
public enum TemplateListReason
{
    Available,
    CaConfigNotSet,
    NotDomainJoined,
    DirectoryUnreachable,
    AccessDenied,
    Unknown,
}

/// <param name="Templates">Empty on every failure path — see <paramref name="Reason"/>.</param>
/// <param name="Reason"><see cref="TemplateListReason.Available"/> only when <paramref
/// name="Templates"/> is a real (possibly still empty — a CA can legitimately have zero templates
/// published) answer from the directory.</param>
/// <param name="Detail">A human-readable reason, always present when <paramref name="Reason"/> is not
/// <see cref="TemplateListReason.Available"/> — what <c>dbdatasync config cert templates</c> prints instead of a
/// list.</param>
public sealed record TemplateListResult(
    IReadOnlyList<string> Templates, TemplateListReason Reason, string? Detail)
{
    internal static TemplateListResult Empty(TemplateListReason reason, string detail) => new([], reason, detail);
}

/// <summary>
/// Best-effort listing of the certificate templates *published on the configured CA* — see the phase 82
/// doc's "Templates are listed on a best-effort basis." Queries the AD configuration partition's
/// enrollment-services object for the CA named in <c>DbDataSync:Certificates:CaConfig</c>
/// (<c>CN=&lt;CA&gt;,CN=Enrollment Services,CN=Public Key Services,CN=Services,CN=Configuration,…</c>)
/// rather than every template in the forest, since a template the CA does not offer cannot be enrolled
/// against and listing it would only produce a confusing failure later.
/// <para>
/// **The word doing the work is "attempt."** Not domain-joined, LDAP unreachable, no read rights on the
/// configuration partition, <c>CaConfig</c> not set yet — every one of those is an ordinary, expected
/// outcome for a tool that might be run from a workgroup machine or a fresh install, and every one
/// returns an empty list and a stated <see cref="TemplateListReason"/>, never an exception and never a
/// blocked command. <c>--template</c> stays free text regardless of whether this succeeded.
/// </para>
/// <para>
/// **Honesty about accuracy**: the HRESULT-to-<see cref="TemplateListReason"/> mapping in
/// <see cref="ClassifyDirectoryFailure"/> is best-effort, written from documented ADSI/COM error codes
/// rather than verified against a real enterprise CA and a real access-denied scenario (this sandbox
/// has neither) — see the phase 82 retrospective's "what is not tested" for the same honesty this
/// project already applies to AD CS enrollment itself. Getting a reason *wrong* still returns
/// <paramref name="TemplateListResult.Detail"/> containing the real exception message, so an operator
/// is never left with only a mislabeled bucket and nothing else.
/// </para>
/// </summary>
public static class CertificateTemplateCatalog
{
    /// <summary>Not marked <see cref="SupportedOSPlatformAttribute"/> — the <c>CaConfig</c>-unset and
    /// non-Windows checks below are ordinary, portable logic, and gating only the actual directory
    /// query (<see cref="ListOnWindows"/>) is what lets a non-Windows caller get a real
    /// <see cref="TemplateListReason"/> back instead of needing its own platform check first.</summary>
    public static TemplateListResult List(string? caConfig)
    {
        if (string.IsNullOrWhiteSpace(caConfig))
        {
            return TemplateListResult.Empty(
                TemplateListReason.CaConfigNotSet, "DbDataSync:Certificates:CaConfig is not set.");
        }

        if (!OperatingSystem.IsWindows())
        {
            return TemplateListResult.Empty(
                TemplateListReason.Unknown, "Certificate management is Windows-only.");
        }

        return ListOnWindows(caConfig);
    }

    [SupportedOSPlatform("windows")]
    private static TemplateListResult ListOnWindows(string caConfig)
    {
        var separatorIndex = caConfig.IndexOf('\\');
        if (separatorIndex < 0)
        {
            return TemplateListResult.Empty(
                TemplateListReason.Unknown,
                $"'{caConfig}' is not in the expected 'CASERVER\\CA Name' shape.");
        }

        var caName = caConfig[(separatorIndex + 1)..];

        // Checked first, and separately from the LDAP query below: a machine that has never been
        // domain-joined should be told exactly that, not handed a generic directory-unreachable
        // message that sends someone checking their network instead of their domain membership.
        try
        {
            Domain.GetComputerDomain();
        }
        catch (Exception ex) when (ex is ActiveDirectoryObjectNotFoundException or ActiveDirectoryOperationException)
        {
            return TemplateListResult.Empty(TemplateListReason.NotDomainJoined, ex.Message);
        }

        try
        {
            using var rootDse = new DirectoryEntry("LDAP://RootDSE");
            var configurationNamingContext = (string)rootDse.Properties["configurationNamingContext"].Value!;

            using var enrollmentService = new DirectoryEntry(
                $"LDAP://CN={caName},CN=Enrollment Services,CN=Public Key Services,CN=Services," +
                $"{configurationNamingContext}");

            var templates = new List<string>();
            foreach (var value in enrollmentService.Properties["certificateTemplates"])
            {
                if (value is string name)
                    templates.Add(name);
            }

            return new TemplateListResult(templates, TemplateListReason.Available, null);
        }
        catch (Exception ex) when (ex is COMException or UnauthorizedAccessException or DirectoryServicesCOMException)
        {
            return TemplateListResult.Empty(ClassifyDirectoryFailure(ex), ex.Message);
        }
    }

    internal static TemplateListReason ClassifyDirectoryFailure(Exception ex)
    {
        if (ex is UnauthorizedAccessException)
            return TemplateListReason.AccessDenied;

        if (ex is COMException com)
        {
            // 0x80072020: "An operations error occurred" / insufficient rights, as ADSI surfaces a
            // permission-denied bind. Everything else COM-shaped from this call is treated as the
            // directory simply not being reachable (the CA server named in CaConfig is offline, DNS
            // can't resolve it, or LDAP/389 is firewalled) — the far more common failure in practice.
            return com.HResult == unchecked((int)0x80072020)
                ? TemplateListReason.AccessDenied
                : TemplateListReason.DirectoryUnreachable;
        }

        return TemplateListReason.Unknown;
    }
}
