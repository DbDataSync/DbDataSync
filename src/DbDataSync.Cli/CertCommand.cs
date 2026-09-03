using System.Runtime.Versioning;
using System.Security.Cryptography.X509Certificates;
using DbDataSync.Api.Auth;
using DbDataSync.Certificates;
using DbDataSync.Core.Config;
using DbDataSync.Core.Git;

namespace DbDataSync.Cli;

/// <summary>
/// <c>dbdatasync cert …</c> — issuance, installation, binding, and renewal of the certificate Kestrel
/// serves TLS with, entirely from the CLI. See the phase 82 doc's "Why the CLI, and why it must be
/// self-sufficient": the admin screen is served over the connection the certificate secures, so
/// bootstrapping it cannot depend on a browser reaching it first, the same reasoning phase 51 applied to
/// <c>dbdatasync service install</c>.
/// <para>
/// Gated once, at the top, the same way <see cref="ServiceCommand.Run"/> gates its own Windows-only
/// surface — nothing under this command runs, or is even reached, off Windows.
/// </para>
/// </summary>
public static class CertCommand
{
    public static int Run(string[] args)
    {
        if (!OperatingSystem.IsWindows())
        {
            Console.Error.WriteLine(
                "Certificate management is Windows-only. On Linux, terminate TLS in front of the " +
                "container, the way any reverse proxy would — see the phase 82 doc's scope.");
            return 1;
        }

        if (args.Length == 0)
        {
            PrintUsage();
            return 1;
        }

        var rest = args[1..];
        return args[0].ToLowerInvariant() switch
        {
            "status" => Status(rest),
            "list" => List(rest),
            "new-self-signed" => NewSelfSigned(rest),
            "enroll" => Enroll(rest),
            "renew" => Renew(rest),
            "retrieve" => Retrieve(rest),
            "templates" => Templates(rest),
            "bind" => Bind(rest),
            var other => Unknown(other),
        };
    }

    [SupportedOSPlatform("windows")]
    private static int Status(string[] args)
    {
        var root = DbDataSyncRoot.Resolve(args);
        var bound = CertificateBinding.Read(root);
        if (bound.Subject is null)
        {
            Console.WriteLine(
                "No certificate is bound. Run 'dbdatasync cert new-self-signed' or 'dbdatasync cert enroll', " +
                "then 'dbdatasync cert bind'.");
            return 0;
        }

        var location = ParseLocation(bound.Location);
        var certificate = CertificateStore.FindBySubject(bound.Subject, location);
        if (certificate is null)
        {
            Console.Error.WriteLine(
                $"dbdatasync.config.yaml binds subject '{bound.Subject}' in {location}\\{bound.Store}, but " +
                "no matching certificate was found there.");
            return 1;
        }

        var info = CertificateInfo.From(certificate);
        var now = DateTimeOffset.UtcNow;

        Console.WriteLine("Bound certificate:");
        Console.WriteLine($"  subject         {info.SubjectCommonName}");
        Console.WriteLine($"  thumbprint      {info.Thumbprint}");
        Console.WriteLine($"  DNS names       {string.Join(", ", info.DnsNames)}");
        Console.WriteLine($"  not before      {info.NotBefore:u}");
        Console.WriteLine($"  not after       {info.NotAfter:u}");
        Console.WriteLine($"  days remaining  {info.DaysRemaining(now)}");
        Console.WriteLine($"  store           {location}\\{bound.Store}");
        Console.WriteLine($"  allow invalid   {bound.AllowInvalid}");

        var account = ResolveAccount(args) ?? "LocalSystem";
        var canRead = string.Equals(account, "LocalSystem", StringComparison.OrdinalIgnoreCase)
            || PrivateKeyAccess.CanRead(certificate, account);
        Console.WriteLine($"  service account '{account}' can read the private key: {(canRead ? "yes" : "no")}");

        return 0;
    }

    [SupportedOSPlatform("windows")]
    private static int List(string[] args)
    {
        var locationArg = CliOptions.Read(args, "--location") ?? "LocalMachine";
        if (!TryParseLocation(locationArg, out var location))
            return 1;

        var certificates = CertificateStore.ListServerAuthCertificates(location);
        if (certificates.Count == 0)
        {
            Console.WriteLine($"No server-authentication certificates in {location}\\My.");
            return 0;
        }

        foreach (var certificate in certificates)
        {
            var info = CertificateInfo.From(certificate);
            Console.WriteLine(
                $"{info.Thumbprint}  {info.SubjectCommonName,-40}  expires {info.NotAfter:yyyy-MM-dd}  " +
                $"[{string.Join(", ", info.DnsNames)}]");
        }

        return 0;
    }

    [SupportedOSPlatform("windows")]
    private static int NewSelfSigned(string[] args)
    {
        var dnsNames = ReadDnsNames(args);
        if (dnsNames is null)
            return 1;

        var days = int.TryParse(CliOptions.Read(args, "--days"), out var parsedDays) ? parsedDays : 397;

        CertificateSpec spec;
        try
        {
            spec = new CertificateSpec(dnsNames[0], dnsNames, days, FriendlyName: "DbDataSync");
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }

        var certificate = CertificateBuilder.CreateSelfSigned(spec);
        var installed = CertificateStore.Install(certificate, StoreLocation.LocalMachine);
        GrantIfNeeded(args, installed);

        Console.WriteLine("Issued and installed a self-signed certificate:");
        PrintIssued(installed);
        return 0;
    }

    [SupportedOSPlatform("windows")]
    private static int Enroll(string[] args)
    {
        var dnsNames = ReadDnsNames(args);
        if (dnsNames is null)
            return 1;

        var root = DbDataSyncRoot.Resolve(args);
        var (caConfig, template) = ResolveCaAndTemplate(root, args);
        if (caConfig is null || template is null)
            return 1;

        CertificateSpec spec;
        try
        {
            // ValidityDays is unused on this path — an AD CS template, not this codebase, decides how
            // long an enrolled certificate is valid for.
            spec = new CertificateSpec(dnsNames[0], dnsNames, ValidityDays: 0, FriendlyName: "DbDataSync");
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }

        return SubmitAndHandle(root, caConfig, template, spec, args);
    }

    [SupportedOSPlatform("windows")]
    private static int Renew(string[] args)
    {
        var root = DbDataSyncRoot.Resolve(args);
        var bound = CertificateBinding.Read(root);
        if (bound.Subject is null)
        {
            Console.Error.WriteLine(
                "No certificate is currently bound to renew. Issue one ('new-self-signed' or 'enroll') " +
                "and bind it first.");
            return 1;
        }

        var location = ParseLocation(bound.Location);
        var current = CertificateStore.FindBySubject(bound.Subject, location);
        if (current is null)
        {
            Console.Error.WriteLine($"Could not find the currently bound certificate (subject '{bound.Subject}') to renew.");
            return 1;
        }

        var dnsNames = CertificateSanReader.GetDnsNames(current);
        if (dnsNames.Count == 0)
        {
            Console.Error.WriteLine("The bound certificate has no SAN entries recorded to renew from.");
            return 1;
        }

        var subjectCommonName = CertificateBinding.SubjectCommonName(current);
        var (caConfig, template) = ResolveCaAndTemplate(root, args, requireBoth: false);

        // Re-enroll, per the phase 82 doc's "Renewal is a re-enroll": a fresh request, not a signed
        // AD CS renewal off the expiring certificate, which is what lets this work identically even
        // when the bound certificate has already expired.
        if (caConfig is not null && template is not null)
        {
            var spec = new CertificateSpec(subjectCommonName, dnsNames, ValidityDays: 0, FriendlyName: "DbDataSync");
            return SubmitAndHandle(root, caConfig, template, spec, args);
        }

        // No CA configured for this deployment — the doc's own "renew" section is written entirely in
        // terms of AD CS, but a self-signed-only deployment still needs a renew command that works, so
        // this falls back to reissuing a fresh self-signed certificate with the bound one's subject and
        // SANs, an extension beyond the doc's literal text rather than leaving renew unusable there.
        var days = int.TryParse(CliOptions.Read(args, "--days"), out var parsedDays) ? parsedDays : 397;
        var selfSignedSpec = new CertificateSpec(subjectCommonName, dnsNames, days, FriendlyName: "DbDataSync");
        var renewed = CertificateBuilder.CreateSelfSigned(selfSignedSpec);
        var installed = CertificateStore.Install(renewed, StoreLocation.LocalMachine);
        GrantIfNeeded(args, installed);

        Console.WriteLine("Renewed (self-signed — no CA is configured):");
        PrintIssued(installed);
        return 0;
    }

    [SupportedOSPlatform("windows")]
    private static int Retrieve(string[] args)
    {
        var requestId = CliOptions.Read(args, "--request-id");
        if (requestId is null)
        {
            Console.Error.WriteLine("Usage: dbdatasync cert retrieve --request-id <id>");
            return 1;
        }

        var root = DbDataSyncRoot.Resolve(args);
        var pending = PendingEnrollmentStore.Find(root, requestId);
        if (pending is null)
        {
            Console.Error.WriteLine(
                $"No pending enrollment recorded here for request id '{requestId}'. It may have been " +
                "submitted from a different machine or repo root.");
            return 1;
        }

        EnrollmentResult result;
        try
        {
            result = AdcsEnrollment.Retrieve(pending.CaConfig, requestId);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Retrieval failed: {ex.Message}");
            return 1;
        }

        var spec = new CertificateSpec(pending.SubjectCommonName, pending.DnsNames, ValidityDays: 0, FriendlyName: "DbDataSync");
        return HandleEnrollmentResult(root, result, pending.KeyName, spec, pending.CaConfig, args);
    }

    private static int Templates(string[] args)
    {
        var root = DbDataSyncRoot.Resolve(args);
        var config = DbDataSyncConfigFile.Read(root);
        var caConfig = CliOptions.Read(args, "--ca") ?? config.GetValueOrDefault("DbDataSync:Certificates:CaConfig");

        var result = CertificateTemplateCatalog.List(caConfig);
        if (result.Reason != TemplateListReason.Available)
        {
            Console.WriteLine($"Could not list templates ({result.Reason}): {result.Detail}");
            Console.WriteLine("You can still pass --template with a known template name to 'dbdatasync cert enroll'.");
            return 0;
        }

        if (result.Templates.Count == 0)
        {
            Console.WriteLine("No templates are published on this CA.");
            return 0;
        }

        foreach (var name in result.Templates)
            Console.WriteLine(name);

        return 0;
    }

    [SupportedOSPlatform("windows")]
    private static int Bind(string[] args)
    {
        var thumbprint = CliOptions.Read(args, "--thumbprint");
        if (thumbprint is null)
        {
            Console.Error.WriteLine(
                "Usage: dbdatasync cert bind --thumbprint <thumbprint> [--location LocalMachine|CurrentUser] " +
                "[--allow-invalid|--no-allow-invalid]");
            return 1;
        }

        var root = DbDataSyncRoot.Resolve(args);
        var locationArg = CliOptions.Read(args, "--location") ?? "LocalMachine";
        if (!TryParseLocation(locationArg, out var location))
            return 1;

        var certificate = CertificateStore.FindByThumbprint(thumbprint, location);
        if (certificate is null)
        {
            Console.Error.WriteLine($"No certificate with thumbprint '{thumbprint}' found in {location}\\My.");
            return 1;
        }

        var isSelfSigned = string.Equals(certificate.Subject, certificate.Issuer, StringComparison.Ordinal);
        var existing = CertificateBinding.Read(root);

        // AllowInvalid is genuinely required for a self-signed certificate — Kestrel validates the
        // chain on load and refuses one it cannot build. The phase 82 doc's own open question is
        // whether binding a later CA-issued certificate should silently clear a flag left on from an
        // earlier self-signed bind; the answer taken here is "report it and leave it" — the flag is
        // only ever touched by an explicit --allow-invalid/--no-allow-invalid, or, on a genuinely first
        // bind for this repo (nothing configured yet), defaulted from whether this certificate is
        // self-signed.
        bool allowInvalid;
        if (CliOptions.Has(args, "--allow-invalid"))
            allowInvalid = true;
        else if (CliOptions.Has(args, "--no-allow-invalid"))
            allowInvalid = false;
        else if (existing.Subject is null)
            allowInvalid = isSelfSigned;
        else
            allowInvalid = existing.AllowInvalid;

        var gitCommitService = new GitCommitService(root);
        CertificateBinding.Bind(root, certificate, allowInvalid, gitCommitService, CurrentUser.SystemAuthor);

        Console.WriteLine(
            $"Bound certificate '{thumbprint}' (subject '{CertificateBinding.SubjectCommonName(certificate)}').");
        Console.WriteLine($"  AllowInvalid: {allowInvalid}");
        if (!isSelfSigned && allowInvalid)
        {
            Console.WriteLine(
                "  Note: this certificate is CA-issued but AllowInvalid is 'true' (left as configured). " +
                "If that was set for a previous self-signed certificate, turn it off explicitly with " +
                "--no-allow-invalid.");
        }

        Console.WriteLine();
        Console.WriteLine("Nothing takes effect until the DbDataSync service restarts.");
        return 0;
    }

    /// <summary>The shared middle of <see cref="Enroll"/>, <see cref="Renew"/>'s AD CS branch, and
    /// <see cref="Retrieve"/>: build a CSR over a named, persisted key (see
    /// <see cref="PendingEnrollmentKeys"/>), submit it, and hand the disposition to
    /// <see cref="HandleEnrollmentResult"/>.</summary>
    [SupportedOSPlatform("windows")]
    private static int SubmitAndHandle(string root, string caConfig, string template, CertificateSpec spec, string[] args)
    {
        var keyName = PendingEnrollmentKeys.NewKeyName();
        using var privateKey = PendingEnrollmentKeys.Create(keyName);
        var csrPem = CertificateBuilder.CreateSigningRequest(spec, privateKey);

        EnrollmentResult result;
        try
        {
            result = AdcsEnrollment.Submit(caConfig, template, csrPem);
        }
        catch (Exception ex)
        {
            PendingEnrollmentKeys.Delete(keyName);
            Console.Error.WriteLine($"Enrollment failed: {ex.Message}");
            return 1;
        }

        return HandleEnrollmentResult(root, result, keyName, spec, caConfig, args);
    }

    /// <summary>Issued, pending, or denied/failed — shared by a fresh <c>enroll</c>/<c>renew</c> and by
    /// <c>retrieve</c> collecting one that came back pending earlier.</summary>
    [SupportedOSPlatform("windows")]
    private static int HandleEnrollmentResult(
        string root, EnrollmentResult result, string keyName, CertificateSpec spec, string caConfig, string[] args)
    {
        switch (result.Outcome)
        {
            case EnrollmentOutcome.Issued:
                var reopenedKey = PendingEnrollmentKeys.Open(keyName)
                    ?? throw new InvalidOperationException(
                        $"The private key for this enrollment ('{keyName}') could not be reopened.");
                X509Certificate2 installed;
                using (reopenedKey)
                {
                    var withKey = result.Certificate!.CopyWithPrivateKey(reopenedKey);
                    installed = CertificateStore.Install(withKey, StoreLocation.LocalMachine);
                }

                PendingEnrollmentKeys.Delete(keyName);
                PendingEnrollmentStore.Remove(root, result.RequestId ?? "");
                GrantIfNeeded(args, installed);

                Console.WriteLine("Certificate issued and installed:");
                PrintIssued(installed);
                return 0;

            case EnrollmentOutcome.Pending:
                PendingEnrollmentStore.Save(root, new PendingEnrollment(
                    result.RequestId!, keyName, spec.SubjectCommonName, spec.DnsNames, caConfig, DateTimeOffset.UtcNow));
                Console.WriteLine(result.Message);
                return 0;

            default:
                PendingEnrollmentKeys.Delete(keyName);
                Console.Error.WriteLine(result.Message);
                return 1;
        }
    }

    private static (string? CaConfig, string? Template) ResolveCaAndTemplate(string root, string[] args, bool requireBoth = true)
    {
        var config = DbDataSyncConfigFile.Read(root);
        var caConfig = CliOptions.Read(args, "--ca") ?? config.GetValueOrDefault("DbDataSync:Certificates:CaConfig");
        var template = CliOptions.Read(args, "--template") ?? config.GetValueOrDefault("DbDataSync:Certificates:Template");

        if (requireBoth && (string.IsNullOrWhiteSpace(caConfig) || string.IsNullOrWhiteSpace(template)))
        {
            Console.Error.WriteLine(
                "Both a CA and a template are needed. Pass --ca/--template, or set " +
                "DbDataSync:Certificates:CaConfig/Template in dbdatasync.config.yaml.");
            return (null, null);
        }

        return (string.IsNullOrWhiteSpace(caConfig) ? null : caConfig, string.IsNullOrWhiteSpace(template) ? null : template);
    }

    /// <summary>Explicit <c>--account</c> wins outright, else the account the installed DbDataSync
    /// service actually runs as, else null (meaning <c>LocalSystem</c>, which needs no grant) — the
    /// phase 82 doc's "which account" resolution order.</summary>
    [SupportedOSPlatform("windows")]
    private static string? ResolveAccount(string[] args) =>
        CliOptions.Read(args, "--account") ?? InstalledServiceAccount.Resolve(ServiceCommand.ServiceName);

    [SupportedOSPlatform("windows")]
    private static void GrantIfNeeded(string[] args, X509Certificate2 certificate)
    {
        var account = ResolveAccount(args);
        if (account is null || string.Equals(account, "LocalSystem", StringComparison.OrdinalIgnoreCase))
            return;

        PrivateKeyAccess.Grant(certificate, account);
        Console.WriteLine($"  granted private-key read access to '{account}'");
    }

    private static void PrintIssued(X509Certificate2 certificate)
    {
        var info = CertificateInfo.From(certificate);
        Console.WriteLine($"  thumbprint  {info.Thumbprint}");
        Console.WriteLine($"  subject     {info.SubjectCommonName}");
        Console.WriteLine($"  DNS names   {string.Join(", ", info.DnsNames)}");
        Console.WriteLine($"  not after   {info.NotAfter:u}");
        Console.WriteLine();
        Console.WriteLine($"  Run 'dbdatasync cert bind --thumbprint {info.Thumbprint}' to put it into service.");
    }

    private static IReadOnlyList<string>? ReadDnsNames(string[] args)
    {
        var raw = CliOptions.Read(args, "--dns");
        if (string.IsNullOrWhiteSpace(raw))
        {
            Console.Error.WriteLine(
                "--dns is required — a comma-separated list of DNS names, e.g. " +
                "--dns dbdatasync.corp.example.com,dbdatasync");
            return null;
        }

        var names = raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (names.Length == 0)
        {
            Console.Error.WriteLine("--dns must name at least one DNS name.");
            return null;
        }

        return names;
    }

    private static StoreLocation ParseLocation(string value) =>
        Enum.TryParse<StoreLocation>(value, ignoreCase: true, out var location) ? location : StoreLocation.LocalMachine;

    private static bool TryParseLocation(string value, out StoreLocation location)
    {
        if (Enum.TryParse(value, ignoreCase: true, out location))
            return true;

        Console.Error.WriteLine($"'{value}' is not a store location. Use LocalMachine or CurrentUser.");
        return false;
    }

    private static int Unknown(string command)
    {
        Console.Error.WriteLine($"Unknown cert command '{command}'.");
        PrintUsage();
        return 1;
    }

    private static void PrintUsage() =>
        Console.Error.WriteLine(
            """
            Usage: dbdatasync cert status [--account <account>]
                   dbdatasync cert list [--location LocalMachine|CurrentUser]
                   dbdatasync cert new-self-signed --dns <names> [--days <n>] [--account <account>]
                   dbdatasync cert enroll --dns <names> [--ca <config>] [--template <name>] [--account <account>]
                   dbdatasync cert renew [--ca <config>] [--template <name>] [--account <account>] [--days <n>]
                   dbdatasync cert retrieve --request-id <id> [--account <account>]
                   dbdatasync cert templates [--ca <config>]
                   dbdatasync cert bind --thumbprint <thumbprint> [--location LocalMachine|CurrentUser] [--allow-invalid|--no-allow-invalid]
            """);
}
