using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Security.Principal;

namespace DbDataSync.Certificates;

/// <summary>
/// The ACL grant the phase 82 doc calls "the part that the original ask did not mention and that will
/// otherwise generate a support ticket per domain install": a certificate in <c>LocalMachine\My</c> has
/// a private-key DACL granting SYSTEM and Administrators, and a service running as a domain account
/// (which <c>dbdatasync service install --account</c> supports) cannot read it — the symptom is a TLS
/// handshake failure at startup that names no permission problem at all.
/// </summary>
[SupportedOSPlatform("windows")]
public static class PrivateKeyAccess
{
    /// <summary>
    /// Grants read access to <paramref name="account"/> on the private key backing
    /// <paramref name="certificate"/>.
    /// <para>
    /// **Reaches the key file directly** — <c>GetRSAPrivateKey()</c> → <see cref="RSACng"/> →
    /// <see cref="CngKey"/> → the key container's on-disk file — because neither
    /// <see cref="X509Certificate2"/> nor <see cref="CngKey"/> exposes an ACL API of its own; the
    /// private key material for the Microsoft Software Key Storage Provider (what
    /// <see cref="CertificateBuilder"/> and AD CS enrollment both use) is an ordinary file under
    /// <c>%ProgramData%\Microsoft\Crypto\Keys</c> for a machine-store key, or the user profile
    /// equivalent for <see cref="StoreLocation.CurrentUser"/>, and an ordinary NTFS DACL is what
    /// actually gates read access to it.
    /// </para>
    /// <para>
    /// Only CNG-backed keys are supported — every key this project creates (self-signed or via AD CS
    /// enrollment) uses the software KSP and so is always <see cref="RSACng"/>. A certificate imported
    /// from elsewhere with a legacy CAPI (CryptoAPI) key is out of scope; <see
    /// cref="NotSupportedException"/> names why rather than silently doing nothing.
    /// </para>
    /// </summary>
    public static void Grant(X509Certificate2 certificate, string account)
    {
        var keyFile = ResolveKeyFilePath(certificate);

        var security = new FileSecurity(keyFile, AccessControlSections.Access);
        security.AddAccessRule(new FileSystemAccessRule(
            new NTAccount(account),
            FileSystemRights.Read,
            AccessControlType.Allow));

        // The instance-method extension (System.IO.FileSystemAclExtensions), not a static
        // File.SetAccessControl(path, security) overload — that overload does not exist; only the
        // FileInfo-based extension does.
        new FileInfo(keyFile).SetAccessControl(security);
    }

    /// <summary>
    /// Whether <paramref name="account"/> already has an explicit read (or broader) allow rule on the
    /// private key — what <c>dbdatasync config cert status</c> and the daily expiry check both report, so a
    /// grant silently removed by a certificate re-issue or a group policy shows up before the next
    /// restart discovers it as a handshake failure instead.
    /// <para>
    /// **Direct-match only**, not a full "resolve group membership and walk every SID" check — the
    /// same simplification <see cref="Grant"/> makes by granting the named account directly rather
    /// than a group. An account genuinely granted access only via group membership reports as unable
    /// to read here even though it actually can; that is the conservative direction for a health
    /// check to be wrong in.
    /// </para>
    /// </summary>
    public static bool CanRead(X509Certificate2 certificate, string account)
    {
        string keyFile;
        try
        {
            keyFile = ResolveKeyFilePath(certificate);
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException or FileNotFoundException)
        {
            return false;
        }

        var security = new FileSecurity(keyFile, AccessControlSections.Access);
        var accountSid = ResolveSid(account);
        if (accountSid is null)
            return false;

        foreach (FileSystemAccessRule rule in security.GetAccessRules(
            includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier)))
        {
            if (rule.AccessControlType != AccessControlType.Allow)
                continue;

            if (!rule.IdentityReference.Equals(accountSid))
                continue;

            if ((rule.FileSystemRights & FileSystemRights.Read) == FileSystemRights.Read)
                return true;
        }

        return false;
    }

    private static SecurityIdentifier? ResolveSid(string account)
    {
        try
        {
            return (SecurityIdentifier)new NTAccount(account).Translate(typeof(SecurityIdentifier));
        }
        catch (Exception ex) when (ex is IdentityNotMappedException or SystemException)
        {
            return null;
        }
    }

    private static string ResolveKeyFilePath(X509Certificate2 certificate)
    {
        if (certificate.GetRSAPrivateKey() is not RSACng rsaCng)
        {
            throw new NotSupportedException(
                $"Certificate '{certificate.Thumbprint}' does not have a CNG-backed private key. " +
                "Only certificates created by this codebase (self-signed or AD CS-issued, both using " +
                "the Microsoft Software Key Storage Provider) support ACL granting.");
        }

        var uniqueName = rsaCng.Key.UniqueName
            ?? throw new InvalidOperationException(
                $"Certificate '{certificate.Thumbprint}' has no persisted key container — its private " +
                "key is ephemeral. Install it via CertificateStore.Install first.");

        // A LocalMachine-store certificate (every real deployment) has a machine-wide key container
        // under %ProgramData%; a CurrentUser-store one (only ever used by this project's own
        // no-elevation-needed tests — see CertificateStoreWindowsTests) has a per-user one under
        // %APPDATA% instead. CngKey.IsMachineKey is which one this particular certificate's key is,
        // rather than assuming — getting this wrong is exactly the "wrong folder" bug a first version
        // of this method had, caught by CertificateStoreWindowsTests itself.
        var baseFolder = rsaCng.Key.IsMachineKey
            ? Environment.SpecialFolder.CommonApplicationData
            : Environment.SpecialFolder.ApplicationData;

        var keysDirectory = Path.Combine(Environment.GetFolderPath(baseFolder), "Microsoft", "Crypto", "Keys");

        var path = Path.Combine(keysDirectory, uniqueName);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException(
                $"Could not find the private key file for certificate '{certificate.Thumbprint}' at " +
                $"'{path}'.", path);
        }

        return path;
    }
}
