using System.Runtime.Versioning;
using System.Security.Cryptography;

namespace DbDataSync.Certificates;

/// <summary>
/// Named, persisted, non-exportable CNG keys for a CSR that might come back
/// <c>CR_DISP_UNDER_SUBMISSION</c> — the piece the phase 82 doc's own text does not spell out, and
/// which <c>dbdatasync config cert retrieve</c> cannot work at all without.
/// <para>
/// **Why a named key, and not the ephemeral one <see cref="CertificateBuilder"/> would otherwise
/// create.** An AD CS submission's CSR carries only the *public* key; the CA never hands the private
/// key back, so whatever process eventually retrieves the issued certificate needs the *same* private
/// key that signed the original request, still around. A request needing certificate-manager approval
/// can sit pending for hours or days, quite possibly outliving the CLI invocation that submitted it —
/// an in-memory RSA object cannot survive that, but a machine-wide CNG key container, opened again by
/// name, can. <see cref="PendingEnrollmentStore"/> is the other half: it remembers which key name goes
/// with which CA request id.
/// </para>
/// <para>
/// **Not tested against a real CA** — same honesty as <see cref="AdcsEnrollment"/> — but the key
/// lifecycle itself (create a named persisted key, open it back up by name, delete it) needs no CA at
/// all and is covered by a <c>Category=Windows</c> test.
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
public static class PendingEnrollmentKeys
{
    private const string KeyNamePrefix = "DbDataSync-PendingEnrollment-";

    /// <summary>A fresh key name, unique per enrollment attempt — the correlation id
    /// <see cref="PendingEnrollmentStore"/> keys its record on.</summary>
    public static string NewKeyName() => $"{KeyNamePrefix}{Guid.NewGuid():N}";

    /// <summary>
    /// Creates and persists an RSA key under <paramref name="keyName"/>, with no export policy set —
    /// the same "nothing in this feature exports a private key" decision
    /// <see cref="CertificateStore.Install"/> makes for an already-issued certificate, applied here to
    /// the key before it has even been signed into one.
    /// <para>
    /// <paramref name="machineKey"/> defaults to true — every real call site (<c>CertCommand</c>'s
    /// <c>enroll</c>/<c>renew</c>/<c>retrieve</c>) issues into <c>LocalMachine\My</c>, which already
    /// needs an elevated prompt the same way <c>dbdatasync service install</c> does, so a machine-wide key
    /// costs nothing there it wasn't already paying. It is <c>false</c> only in
    /// <c>PendingEnrollmentKeysWindowsTests</c>, which — like the doc's own "install into
    /// <c>CurrentUser\My</c>" test — deliberately needs no elevation to run.
    /// </para>
    /// </summary>
    public static RSA Create(string keyName, bool machineKey = true)
    {
        var creationParameters = new CngKeyCreationParameters
        {
            Provider = CngProvider.MicrosoftSoftwareKeyStorageProvider,
            KeyUsage = CngKeyUsages.AllUsages,
            KeyCreationOptions = machineKey ? CngKeyCreationOptions.MachineKey : CngKeyCreationOptions.None,
            ExportPolicy = CngExportPolicies.None,
        };
        creationParameters.Parameters.Add(new CngProperty(
            "Length", BitConverter.GetBytes(CertificateSpec.DefaultKeySizeBits), CngPropertyOptions.None));

        var cngKey = CngKey.Create(CngAlgorithm.Rsa, keyName, creationParameters);
        return new RSACng(cngKey);
    }

    /// <summary>Reopens a key created by <see cref="Create"/> — null if no such key exists, which is
    /// an ordinary outcome for an unrecognised or already-collected request id, not a reason to
    /// throw.</summary>
    public static RSA? Open(string keyName, bool machineKey = true)
    {
        var openOptions = machineKey ? CngKeyOpenOptions.MachineKey : CngKeyOpenOptions.None;
        if (!CngKey.Exists(keyName, CngProvider.MicrosoftSoftwareKeyStorageProvider, openOptions))
            return null;

        var cngKey = CngKey.Open(keyName, CngProvider.MicrosoftSoftwareKeyStorageProvider, openOptions);
        return new RSACng(cngKey);
    }

    /// <summary>Removes the persisted key — called once an enrollment resolves (issued, denied, or
    /// failed) so a pending key never outlives the request it was for.</summary>
    public static void Delete(string keyName, bool machineKey = true)
    {
        var openOptions = machineKey ? CngKeyOpenOptions.MachineKey : CngKeyOpenOptions.None;
        if (!CngKey.Exists(keyName, CngProvider.MicrosoftSoftwareKeyStorageProvider, openOptions))
            return;

        using var cngKey = CngKey.Open(keyName, CngProvider.MicrosoftSoftwareKeyStorageProvider, openOptions);
        cngKey.Delete();
    }
}
